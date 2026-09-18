using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;   // app-94sk T6b: Quaternion/Vector3, the doll's own arithmetic, replicated by two pins

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
            bool hit = HitVolumes.Resolve(parts, in table, TestWorlds.RestPoseOf(in table), bodyTilt: float2.zero,
                bodyOrigin: new float3(6f, 0f, 0f), bodyFacingSin: 0f, bodyFacingCos: 1f,
                p0, p1, projRadius: cfg.Weapon.ProjectileRadius,
                out HitZone zone, out _, out _, out _, out float t);
            Assert.IsTrue(hit, "луч не встретил ни одного объёма — фикстура мерит не свой предмет");
            Assert.AreEqual(HitZone.Head, zone, "корпус отобрал попадание у головы — приоритета зоны нет");
            // Premise AS A PROPERTY: the torso must be struck EARLIER, or the test
            // is green without a priority ladder at all.
            bool torsoFirst = HitVolumes.Resolve(new[] { parts[1] }, in table, TestWorlds.RestPoseOf(in table), float2.zero,
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
            bool hitFirst = HitVolumes.Resolve(new[] { parts[0] }, in table, TestWorlds.RestPoseOf(in table), float2.zero,
                float3.zero, 0f, 1f, p0, p1, 0f, out _, out _, out _, out _, out float tFirst);
            bool hitSecond = HitVolumes.Resolve(new[] { parts[1] }, in table, TestWorlds.RestPoseOf(in table), float2.zero,
                float3.zero, 0f, 1f, p0, p1, 0f, out _, out _, out _, out _, out float tSecond);
            Assert.IsTrue(hitFirst && hitSecond, "премисса: обе капсулы обязаны быть задеты");
            Assert.AreEqual(tFirst, tSecond, 1e-6f, "премисса: t обеих совпадают — иначе решает не PartId");

            bool hit = HitVolumes.Resolve(parts, in table, TestWorlds.RestPoseOf(in table), float2.zero, float3.zero, 0f, 1f,
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
            Assert.IsTrue(HitVolumes.Resolve(parts, in table, TestWorlds.RestPoseOf(in table), float2.zero, origin, 0f, 1f,
                    p0, p1, projR, out HitZone zone, out _, out _, out _, out _),
                "премисса фикстуры: с частями и таблицей этот шаг обязан попадать");
            Assert.AreNotEqual(HitZone.None, zone, "премисса фикстуры: попадание названо зоной");

            Assert.IsFalse(HitVolumes.Resolve(System.Array.Empty<HitPart>(), in table, TestWorlds.RestPoseOf(in table),
                    float2.zero, origin, 0f, 1f, p0, p1, projR,
                    out zone, out _, out _, out _, out _),
                "пустой набор объёмов разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "пустой набор вернул небезразличную зону");

            Assert.IsFalse(HitVolumes.Resolve(null, in table, TestWorlds.RestPoseOf(in table), float2.zero, origin, 0f, 1f,
                    p0, p1, projR, out zone, out _, out _, out _, out _),
                "отсутствующий набор объёмов разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "отсутствующий набор вернул небезразличную зону");

            PoseTable empty = default;
            Assert.IsFalse(HitVolumes.Resolve(parts, in empty, System.Array.Empty<float3>(), float2.zero, origin, 0f, 1f,
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
            Assert.IsTrue(HitVolumes.Resolve(parts, in table, TestWorlds.RestPoseOf(in table), float2.zero, float3.zero, 0f, 1f,
                    new float3(-2f, lateral, boneHeight), new float3(2f, lateral, boneHeight), projR,
                    out _, out _, out float hitHeight, out _, out _),
                "кость не найдена там, где она стоит: высота тела не доехала до высоты мира");
            Assert.AreEqual(boneHeight, hitHeight, 1e-4f,
                "высота контакта пришла не из шага — её берут из .z, как и весь мир");

            // ...and LOW, FAR ASIDE — where the bone would have gone had the two
            // components changed places.
            Assert.IsFalse(HitVolumes.Resolve(parts, in table, TestWorlds.RestPoseOf(in table), float2.zero, float3.zero, 0f, 1f,
                    new float3(-2f, boneHeight, lateral), new float3(2f, boneHeight, lateral), projR,
                    out _, out _, out _, out _, out _),
                "попадание засчитано по ЛЕЖАЩЕЙ кости — .y и .z обменялись местами в ToWorld");
        }

        // ------------------------------------------------ app-94sk T6b (spec §3.6/§3.8)

        [Test]
        public void ATurnedBodyPresentsItsLegsWhereItFaces()   // fixture 6, witness of M331
        {
            // ⭐⭐ THE TURN CHANGES THE OUTCOME. A chaser's legs stand 0.36-0.40 m
            // off his axis, one to each side of his FORWARD; a level shot down
            // the world's x axis through his origin crosses both legs while he
            // faces +y (the table's own orientation) and passes BETWEEN them
            // once he faces +x -- his legs now stand at y = +/-0.38, a
            // shoulder-width off the line. Through the world, so the witness
            // covers the caller handing the course to the resolver, not the
            // resolver alone; the direct pin of the turn's arithmetic against
            // the doll's is the next fixture.
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);   // BEFORE the constructor
            const float shotHeight = 0.30f;   // under the chest capsule's floor (0.52 - 0.12), across the shins

            HitZone Shoot(float2 course)
            {
                var w = new SimulationWorld(6, cfg);
                w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
                var m = w.Mobs[0]; m.Dir = course; w.SetMobForTest(0, m);
                w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                    new float2(cfg.Weapon.ProjectileSpeed, 0f), shotHeight, velZ: 0f,
                    cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime);
                TestWorlds.RunUntilProjectilesDie(w);
                return TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e)
                    ? e.Zone : HitZone.None;
            }

            // PREMISE AS A PROPERTY: every leg volume the shot's height can
            // meet stands further off the axis than the round reaches, or
            // facing +x would still be a hit -- asked of the table, not
            // written as a number.
            int legsAtHeight = 0;
            foreach (HitPart part in cfg.Chaser.Parts)
            {
                if (part.Zone != HitZone.Legs) continue;
                if (part.RestBottom > shotHeight + cfg.Weapon.ProjectileRadius
                    || part.RestTop < shotHeight - cfg.Weapon.ProjectileRadius) continue;
                legsAtHeight++;
                float offset = math.min(math.abs(cfg.Chaser.Poses.Bones[part.BoneA].x),
                    math.abs(cfg.Chaser.Poses.Bones[part.BoneB].x));
                // ⚠ TIGHT, AND SAID: the binding volume is the right shin,
                // whose lower-leg bone stands 0.3605 off the axis against
                // 0.2019 + 0.12 = 0.3219 -- 0.039 m; the actual crossing at
                // 0.30 m clears the line by 0.048 m. Exact arithmetic on
                // fixed numbers, not a flaky margin -- but a rig re-measure
                // could close it, and this message is where that will show.
                Assert.Greater(offset, part.Radius + cfg.Weapon.ProjectileRadius,
                    $"премисса фикстуры: объём ног {part.PartId} стоит дальше от оси, чем достаёт снаряд "
                    + $"(запас {offset - part.Radius - cfg.Weapon.ProjectileRadius:F3} м)");
            }
            Assert.Greater(legsAtHeight, 0, "премисса фикстуры: на высоте выстрела есть объёмы ног");

            Assert.AreEqual(HitZone.Legs, Shoot(new float2(0f, 1f)),
                "премисса фикстуры: тело курсом +y (ориентация таблицы) обязано подставить ноги под выстрел вдоль x");
            Assert.AreEqual(HitZone.None, Shoot(new float2(1f, 0f)),
                "тело курсом +x всё ещё подставляет ноги под выстрел вдоль x — объёмы не поворачиваются с телом (M331)");
        }

        [Test]
        public void TheVolumesTurnTheWayTheDollTurns()   // the yaw law against Presentation's
        {
            // ⛔⛔ TWO HOMES OF ONE FACT, PINNED AGAINST EACH OTHER. The doll
            // turns by `LookRotation(dirUnity, up) * AngleAxis(YawOffsetDeg,
            // up)` (PlayerVisual.FacingAlong / MobVisual.Sync), with 180 deg
            // for the collector's UAL2 rig and 0 for the mechs
            // (GameFeelConfig's C# defaults, the test-side source of those two
            // numbers). The hit volumes turn by HitVolumes.YawOf over
            // BakedClips' rig forwards. If the two disagree, a collector's
            // slide presents its legs where the doll shows its head.
            // The doll's arithmetic is REPLICATED here on purpose (lesson 427):
            // a pin that called Presentation's own method would prove that
            // Presentation agrees with itself.
            var feel = ScriptableObject.CreateInstance<Ring.Data.GameFeelConfig>();
            try
            {
                // An off-axis, asymmetric bone and an off-axis, asymmetric
                // course: a mirror or a 90-degree slip is visible on every
                // component.
                var bone = new float3(0.30f, 1.00f, 0.70f);
                float2 course = math.normalize(new float2(0.6f, -0.8f));
                AssertTurnsLikeTheDoll(bone, course, BakedClips.CollectorForward,
                    feel.PlayerYawOffsetDeg, "сборщик");
                AssertTurnsLikeTheDoll(bone, course, BakedClips.MobForward,
                    feel.MechYawOffsetDeg, "мех");
                // And the rest orientation is what every caller before T6b
                // handed in: a mob facing +y turns by the identity.
                HitVolumes.YawOf(new float2(0f, 1f), BakedClips.MobForward, out float s, out float c);
                Assert.AreEqual(0f, s, 1e-6f, "курс +y у рига +z — тождественный поворот (sin)");
                Assert.AreEqual(1f, c, 1e-6f, "курс +y у рига +z — тождественный поворот (cos)");
            }
            finally
            {
                Object.DestroyImmediate(feel);
            }
        }

        static void AssertTurnsLikeTheDoll(float3 bone, float2 course, float2 rigForward,
            float yawOffsetDeg, string body)
        {
            Vector3 dirUnity = new Vector3(course.x, 0f, course.y);   // SimSpace.ToWorld's own mapping
            Quaternion facing = Quaternion.LookRotation(dirUnity, Vector3.up)
                * Quaternion.AngleAxis(yawOffsetDeg, Vector3.up);
            Vector3 doll = facing * new Vector3(bone.x, bone.y, bone.z);

            HitVolumes.YawOf(course, rigForward, out float s, out float c);
            float3 ours = HitVolumes.ToWorld(bone, float3.zero, s, c, float2.zero);
            Assert.AreEqual(doll.x, ours.x, 1e-4f, $"{body}: план x объёма разошёлся с куклой");
            Assert.AreEqual(doll.z, ours.y, 1e-4f, $"{body}: план y объёма разошёлся с куклой (Unity z)");
            Assert.AreEqual(doll.y, ours.z, 1e-4f, $"{body}: высота объёма разошлась с куклой");
        }

        [Test]
        public void ALeanIsAppliedInTheWorldAfterTheTurn()   // the tilt's order and sign against the doll's
        {
            // ⛔⛔ THE ORDER IS A CONTRACT, AND THE PLAN'S SNIPPET HAD IT
            // BACKWARDS: the doll composes `TiltRotation(m.Tilt) * facing`,
            // tilt OUTERMOST, about an axis fixed in the WORLD (Cross(up,
            // tiltDir)) -- because `Tilt` says which way in the world the body
            // goes down, not which way in its own frame. Applied in the body
            // frame first, a turned body would fall a quarter turn off the
            // side the blow sent it. A body turned to face +x and leaning
            // towards +x: a bone standing on its axis at height 1 has to land
            // sin(0.9) towards +x and cos(0.9) high, whichever way the body
            // faces. Replicated, not called, for the reason the pin above
            // gives.
            var bone = new float3(0f, 1f, 0f);
            float2 course = new float2(1f, 0f);
            float2 tilt = new float2(0.9f, 0f);
            Vector3 tiltDirUnity = new Vector3(1f, 0f, 0f);
            Quaternion facing = Quaternion.LookRotation(new Vector3(course.x, 0f, course.y), Vector3.up)
                * Quaternion.AngleAxis(0f, Vector3.up);
            Quaternion lean = Quaternion.AngleAxis(0.9f * Mathf.Rad2Deg,
                Vector3.Cross(Vector3.up, tiltDirUnity));
            Vector3 doll = (lean * facing) * new Vector3(bone.x, bone.y, bone.z);
            Assert.AreEqual(math.sin(0.9f), doll.x, 1e-4f,
                "премисса реплики: кукла кладёт макушку в сторону крена");

            HitVolumes.YawOf(course, BakedClips.MobForward, out float s, out float c);
            float3 ours = HitVolumes.ToWorld(bone, float3.zero, s, c, tilt);
            Assert.AreEqual(doll.x, ours.x, 1e-4f, "крен положил кость не туда, куда кладёт кукла (план x)");
            Assert.AreEqual(doll.z, ours.y, 1e-4f, "крен положил кость не туда, куда кладёт кукла (план y)");
            Assert.AreEqual(doll.y, ours.z, 1e-4f, "крен не опустил кость на высоту куклы");
        }
    }
}
