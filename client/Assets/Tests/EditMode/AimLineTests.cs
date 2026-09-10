using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The line of fire as a pure function (app-461s T1, spec §4.4 tests 1-19
    /// and 24-26). Tests 20-23 live in WeaponTests instead: they examine the
    /// CONE, whose home is Spread, and HipSpread_RunAndSlideMultipliers
    /// already stands there proving the movement multipliers. Test 27 lives in
    /// AllocationTests.
    public class AimLineTests
    {
        const float Eps = 1e-4f;

        /// THE FIXTURE IS BUILT HERE RATHER THAN TAKEN OUT OF SimulationWorld:
        /// Solve takes a RenderSnapshot, and building one by hand is cheaper
        /// and more honest than ticking a world for the sake of two fields.
        /// The set already builds frames exactly this way -- ResultsTests and
        /// FramePresenceTests both go through the one constructor
        /// `new RenderSnapshot(in cfg)`, which sizes its arrays from the
        /// arena's caps.
        static RenderSnapshot Snap(in SimConfig cfg, int players = 1, int mobs = 0)
        {
            var s = new RenderSnapshot(in cfg)
                { PlayerCount = players, MobCount = mobs, LocalPlayerIndex = 0 };
            for (int i = 0; i < players; i++) s.Players[i] = new PlayerState { Alive = true };
            return s;
        }

        /// The candidate buffer `Solve` borrows from its caller (plan deviation
        /// 7): the two-stage scan with a re-scan needs room for the bodies it
        /// ranks, and a pure function has no world of its own to preallocate it
        /// in.
        ///
        /// ⛔ SIZE IS MaxMobs + MaxPlayers, BODIES AND NOTHING ELSE, and the
        /// neighbor's spare slots are deliberately not copied: the world's own
        /// projectile buffer packs the barrier, the rim and the floor into the
        /// same array, while here the barrier and the rim arrive through the
        /// projectile step and never enter this buffer at all -- and a floor
        /// candidate cannot exist by construction, the line being horizontal.
        ///
        /// ⚠ A FRESH ARRAY PER CALL IS LEGAL HERE, AND ONLY HERE: the
        /// no-allocation promise of this task is pinned by test 27 in
        /// AllocationTests, whose fixture allocates its own buffer BEFORE the
        /// measurement starts.
        static (float t, int kind, int index)[] Scratch(in SimConfig cfg)
            => new (float t, int kind, int index)[cfg.Arena.MaxMobs + cfg.Arena.MaxPlayers];

        /// A barrier top bound to HOLD the line: the muzzle's height, the
        /// round's own radius on top of it (the height gate pads by that at
        /// both ends) and a meter to spare. ⚠ Every fixture here states its own
        /// Arena.BarrierTop -- the shared baseline keeps it at zero for the
        /// goldens' sake -- and three of them want the SAME number, so it is
        /// written once instead of three times.
        static float BarrierTopThatHolds(in SimConfig cfg)
            => cfg.Hero.MuzzleHeight + cfg.Weapon.ProjectileRadius + 1f;

        [Test]
        public void TheLineIsHorizontalAtTheMuzzleHeight()   // test 1, M309
        {
            SimConfig cfg = TestConfigs.OpenField();
            var snap = Snap(in cfg);
            AimLineSolution standing = AimLine.Solve(float2.zero, new float2(10f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(cfg.Hero.MuzzleHeight, standing.Height, Eps,
                "высота линии не равна высоте дула стоя");

            AimLineSolution sliding = AimLine.Solve(float2.zero, new float2(10f, 0f),
                cfg.Hero.SlideMuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(cfg.Hero.SlideMuzzleHeight, sliding.Height, Eps,
                "в слайде линия не опустилась — высота приходит параметром, а не тернаром внутри");
            // ⚠ THE PREMISE IS A PROPERTY, NOT A LITERAL (307/308): the two
            // heights have to differ, or the test is green on either of them.
            Assert.Less(cfg.Hero.SlideMuzzleHeight, cfg.Hero.MuzzleHeight,
                "премисса фикстуры: слайдовое дуло ниже стоячего");
        }

        [Test]
        public void AnEmptyArenaStopsTheLineAtTheRangeLimit()   // test 2, M304
        {
            SimConfig cfg = TestConfigs.OpenField();
            AimLineSolution line = AimLine.Solve(float2.zero, new float2(10f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Range, line.Stop, "упор не в предел дальности");
            // The expectation is a FIXTURE EXPRESSION: 35 * 1.5 = 52.5 m, not
            // the 78.75 m the game's own asset would give (lesson 699).
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime,
                line.Length, Eps, "предел дальности не равен сроку жизни снаряда");
        }

        [Test]
        public void TheLineStartsAtTheMuzzle_NotAtTheHero()   // test 3, M314
        {
            SimConfig cfg = TestConfigs.OpenField();
            var hero = new float2(3f, -2f);
            AimLineSolution line = AimLine.Solve(hero, new float2(13f, -2f),
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.AreEqual(cfg.Weapon.MuzzleOffset, math.distance(hero, line.Start), Eps,
                "начало линии не отодвинуто на MuzzleOffset");
            // And it sits ON the collector-to-cursor segment rather than off to
            // one side of it.
            Assert.AreEqual(1f, math.dot(math.normalize(line.Start - hero), line.Dir), Eps,
                "дуло отодвинуто не вдоль линии прицеливания");
        }

        [Test]
        public void TheRingWallStopsTheLine_WhenNothingElseDoes()   // test 4, M311
        {
            // ⛔ THE RIM IS OUT OF REACH FROM THE CENTER OF THE SHARED FIXTURE,
            // and without this line the test would be red on CORRECT code:
            // Arena.Radius is 173 m against a range limit of 52.5 m. Shrinking
            // the world is the set's own helper for exactly this need, and it
            // carries the zone boundaries in with it.
            SimConfig cfg = TestConfigs.OpenField();
            TestConfigs.ShrinkArena(ref cfg, 30f);
            var cursor = new float2(40f, 0f);
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;

            // ⚠ TWO PREMISES, AND BOTH ARE PROPERTIES OF THE FIXTURE: the
            // cursor lies beyond the rim, and the rim itself is inside the
            // range limit. Drop either and the line stops at the range limit,
            // examining nothing.
            Assert.Greater(math.length(cursor), cfg.Arena.Radius,
                "премисса фикстуры: курсор за ободом арены");
            Assert.Less(cfg.Arena.Radius, reach,
                "премисса фикстуры: обод достижим в пределах дальности");

            AimLineSolution line = AimLine.Solve(float2.zero, cursor,
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.RingWall, line.Stop, "упор не в обод арены");
            Assert.Less(line.Length, reach, "обод не укоротил линию против предела дальности");
        }

        [Test]
        public void ACursorNearerThanTheMuzzleDoesNotFlipTheDirection()   // test 16, M308
        {
            SimConfig cfg = TestConfigs.OpenField();
            var hero = new float2(2f, 1f);
            // The cursor sits INSIDE the muzzle offset, which is the whole
            // point of the fixture: a direction measured from the muzzle
            // instead of from the collector points backwards from here.
            float2 cursor = hero + new float2(0.5f * cfg.Weapon.MuzzleOffset, 0f);
            Assert.Less(math.distance(hero, cursor), cfg.Weapon.MuzzleOffset,
                "премисса фикстуры: курсор ближе дула");

            AimLineSolution line = AimLine.Solve(hero, cursor, cfg.Hero.MuzzleHeight,
                in cfg, Snap(in cfg), 0, Scratch(in cfg));
            float2 expected = math.normalize(cursor - hero);
            Assert.Greater(math.dot(line.Dir, expected), 0f,
                "направление линии развернулось на близком курсоре");
        }

        [Test]
        public void ADegenerateAimFallsBack_NotToNaNAndNotToZero()   // test 17
        {
            SimConfig cfg = TestConfigs.OpenField();
            var hero = new float2(-4f, 7f);
            // The cursor is exactly on the collector, so the aim vector is
            // degenerate and normalizesafe has to answer its fallback rather
            // than a zero vector or a NaN one.
            float2 cursor = hero;
            Assert.AreEqual(0f, math.lengthsq(cursor - hero), Eps,
                "премисса фикстуры: направление вырождено — курсор в позиции сборщика");

            AimLineSolution line = AimLine.Solve(hero, cursor, cfg.Hero.MuzzleHeight,
                in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.IsFalse(math.any(math.isnan(line.Dir)),
                "вырожденный ввод дал нечисловое направление");
            Assert.AreEqual(1f, math.length(line.Dir), Eps,
                "вырожденный ввод дал не единичное направление");
        }

        [Test]
        public void AMuzzleInsideABodyCircleIsPinnedExplicitly()   // test 18
        {
            // ⛔ THE BEHAVIOR IS THE PLAN'S CHOICE, NOT THE IMPLEMENTER'S. A
            // muzzle inside a body circle makes the swept-circle test answer
            // t = 0, and the line answers Body at zero length with the zone
            // read off that body's parts at the muzzle's height. The argument
            // is the one the whole task stands on: the line has to answer what
            // the SHOT will answer, and a round leaving this muzzle hits this
            // body at once. This is the "pressed against cover or against a
            // mob" case the spec's first review round asked about.
            SimConfig cfg = TestConfigs.OpenField();
            var hero = float2.zero;
            var cursor = new float2(10f, 0f);
            float2 muzzle = hero + new float2(cfg.Weapon.MuzzleOffset, 0f);
            float2 bodyPos = muzzle + new float2(0.2f, 0f);
            var snap = Snap(in cfg, mobs: 1);
            snap.Mobs[0] = new MobState
                { Id = 1, Type = MobType.Chaser, Pos = bodyPos, Hp = cfg.Chaser.MaxHp };

            // ⚠ PREMISES AS PROPERTIES: the body's padded circle swallows the
            // muzzle, and the muzzle's height falls inside the part whose zone
            // the expectation is then read from -- no literal zone, and no
            // literal radius.
            Assert.Less(math.distance(muzzle, bodyPos),
                cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: дуло внутри падированного круга тела");
            HitPart corpus = cfg.Chaser.Parts[1];
            Assert.Greater(cfg.Hero.MuzzleHeight, corpus.Bottom,
                "премисса фикстуры: высота дула выше низа корпуса цели");
            Assert.Less(cfg.Hero.MuzzleHeight, corpus.Top,
                "премисса фикстуры: высота дула ниже верха корпуса цели");

            AimLineSolution line = AimLine.Solve(hero, cursor, cfg.Hero.MuzzleHeight,
                in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop, "дуло внутри круга тела не дало упора в тело");
            Assert.AreEqual(0f, line.Length, Eps, "упор в тело из его же круга не нулевой длины");
            Assert.AreEqual(corpus.Zone, line.Zone,
                "зона под осью не прочитана по частям на высоте дула");
        }

        // ---- second group (Step 1a): bodies and stops ----

        /// One obstacle circle and one chaser, both on the +x axis at the given
        /// distances from the origin, in a fixture whose barrier is stated tall
        /// enough to hold the line. ⚠ The height is STATED rather than
        /// inherited, the idiom BarrierHeightTests' header spells out: the
        /// shared baseline keeps Arena.BarrierTop at 0 for the goldens' sake.
        static (SimConfig cfg, RenderSnapshot snap) BarrierAndBody(float obstacleX, float bodyX)
        {
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Arena.BarrierTop = BarrierTopThatHolds(in cfg);
            TestConfigs.PutObstacle(ref cfg, new float2(obstacleX, 0f), 1f);
            RenderSnapshot snap = Snap(in cfg, mobs: 1);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser,
                Pos = new float2(bodyX, 0f), Hp = cfg.Chaser.MaxHp };
            return (cfg, snap);
        }

        [Test]
        public void ABodyOnTheLineStopsItBeforeTheWallBehind()   // test 5, M301
        {
            const float wallX = 20f;
            SimConfig cfg = TestConfigs.OpenField();
            // ⚠ THE FIXTURE STATES ITS OWN BarrierTop -- a wall that has to
            // HOLD must be stated as holding, and the shared baseline states
            // nothing (it keeps the number at 0 for the goldens' sake).
            cfg.Arena.BarrierTop = BarrierTopThatHolds(in cfg);
            TestConfigs.PutWall(ref cfg, new float2(wallX, -4f), new float2(wallX, 4f), 0.5f);
            var body = new float2(10f, 0f);
            var snap = Snap(in cfg, mobs: 1);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser, Pos = body,
                Hp = cfg.Chaser.MaxHp };
            var muzzle = new float2(cfg.Weapon.MuzzleOffset, 0f);
            float toWall = wallX - cfg.Weapon.MuzzleOffset;

            Assert.Greater(cfg.Arena.BarrierTop,
                cfg.Hero.MuzzleHeight + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: стена выше луча и обязана держать");
            Assert.Less(math.abs(body.y - muzzle.y),
                cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: тело падированным кругом накрывает ось линии");
            Assert.Less(math.distance(muzzle, body), toWall,
                "премисса фикстуры: тело стоит ближе стены");

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop, "тело на линии её не остановило");
            Assert.Less(line.Length, toWall, "линия прошла тело насквозь до стены за ним");
        }

        [Test]
        public void ABodyOffTheLineDoesNotStopIt()   // test 6, M315
        {
            SimConfig cfg = TestConfigs.OpenField();
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            float padded = cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius;
            // ⛔ BOTH SIDES OF THE PADDED BOUNDARY, AND THAT IS WHAT KILLS THE
            // MUTANT: dropping the projectile's own radius only NARROWS the
            // threshold, so "a body off the line does not hold it" stays true
            // on the mutant and it would survive a one-sided fixture. The step
            // is an expression of the pad itself -- the whole band is one
            // ProjectileRadius wide.
            float step = 0.25f * cfg.Weapon.ProjectileRadius;
            Assert.Greater(padded - step, cfg.Chaser.Radius,
                "премисса фикстуры: ближнее смещение уже вне голого радиуса тела — держит только пад радиуса снаряда");
            Assert.Less(step, cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: шаг меньше самого пада — половины лежат по разные стороны его границы");

            var inside = Snap(in cfg, mobs: 1);
            inside.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser,
                Pos = new float2(10f, padded - step), Hp = cfg.Chaser.MaxHp };
            AimLineSolution held = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, inside, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, held.Stop,
                "смещение внутри пада тело не задело — радиус снаряда в пороге не участвует");

            var outside = Snap(in cfg, mobs: 1);
            outside.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser,
                Pos = new float2(10f, padded + step), Hp = cfg.Chaser.MaxHp };
            AimLineSolution clear = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, outside, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Range, clear.Stop, "тело в стороне удержало линию");
            Assert.AreEqual(reach, clear.Length, Eps,
                "линия мимо тела не дошла до предела дальности");
        }

        [Test]
        public void ARefusedBodyDoesNotScreenTheOneBehindIt()   // test 7, M307
        {
            SimConfig cfg = TestConfigs.OpenField();
            HitPart gunnerLegs = cfg.Gunner.Parts[0];
            HitPart gunnerCorpus = cfg.Gunner.Parts[1];
            HitPart chaserCorpus = cfg.Chaser.Parts[1];
            // The near body is REFUSED by a sideways miss of its narrow part:
            // the axis clips its body circle and clears its legs, which are the
            // only part standing at the muzzle's height.
            var near = new float2(6f, 0.55f);
            var far = new float2(12f, 0f);
            var snap = Snap(in cfg, mobs: 2);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Gunner, Pos = near,
                Hp = cfg.Gunner.MaxHp };
            snap.Mobs[1] = new MobState { Id = 2, Type = MobType.Chaser, Pos = far,
                Hp = cfg.Chaser.MaxHp };
            var muzzle = new float2(cfg.Weapon.MuzzleOffset, 0f);

            Assert.Greater(near.y, gunnerLegs.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: ось мимо узкой части ближнего тела");
            Assert.Less(near.y, cfg.Gunner.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: круг ближнего тела ось всё же задевает");
            Assert.Less(cfg.Hero.MuzzleHeight, gunnerCorpus.Bottom,
                "премисса фикстуры: корпус ближнего тела выше луча — на этой высоте у него только узкая часть");
            Assert.Less(near.x, far.x, "премисса фикстуры: отказавшее тело — ближнее");

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop, "перескан не нашёл дальнее тело");
            Assert.AreEqual(chaserCorpus.Zone, line.Zone,
                "ответ пришёл не от дальнего тела — зона не его");
            Assert.Greater(line.Length, math.distance(muzzle, near),
                "линия встала на отказавшем ближнем теле");
        }

        [Test]
        public void ABarrierNearerThanABodyWins_AndViceVersa()   // test 8, M312
        {
            var near = BarrierAndBody(obstacleX: 8f, bodyX: 16f);
            Assert.Greater(near.cfg.Arena.BarrierTop,
                near.cfg.Hero.MuzzleHeight + near.cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: барьер выше луча и обязан держать");
            AimLineSolution barrierFirst = AimLine.Solve(float2.zero, new float2(30f, 0f),
                near.cfg.Hero.MuzzleHeight, in near.cfg, near.snap, 0, Scratch(in near.cfg));
            Assert.AreEqual(AimStop.Barrier, barrierFirst.Stop,
                "ближний барьер не победил дальнее тело");

            var far = BarrierAndBody(obstacleX: 16f, bodyX: 8f);
            AimLineSolution bodyFirst = AimLine.Solve(float2.zero, new float2(30f, 0f),
                far.cfg.Hero.MuzzleHeight, in far.cfg, far.snap, 0, Scratch(in far.cfg));
            Assert.AreEqual(AimStop.Body, bodyFirst.Stop,
                "ближнее тело не победило дальний барьер");
        }

        [Test]
        public void AnExactTieGoesToTheCandidateAskedFirst()   // test 9, M313
        {
            // ⛔ THE WINNER HAS TO BE OBSERVABLE, AND TWO MIRRORED TWINS WOULD
            // NOT BE: same Stop, same Zone, same Length whichever of them wins,
            // and the mutant with a non-strict comparison survives. So the two
            // candidates are a chaser and a gunner -- one body radius, but
            // DIFFERENT parts at the muzzle's height, hence different zones.
            // ⚠ ASKED FIRST MEANS THE SMALLER INDEX: mobs are asked before
            // collectors and in index order, so the chaser at index 0 is the
            // one a strict comparison keeps.
            // ⚠ The axis runs strictly along a coordinate axis; tilt it and the
            // two t values drift apart in the last bits and there is no tie
            // left to break.
            SimConfig cfg = TestConfigs.OpenField();
            HitPart chaserCorpus = cfg.Chaser.Parts[1];
            HitPart gunnerLegs = cfg.Gunner.Parts[0];
            const float d = 0.4f;
            var snap = Snap(in cfg, mobs: 2);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser,
                Pos = new float2(10f, d), Hp = cfg.Chaser.MaxHp };
            snap.Mobs[1] = new MobState { Id = 2, Type = MobType.Gunner,
                Pos = new float2(10f, -d), Hp = cfg.Gunner.MaxHp };

            Assert.AreEqual(cfg.Chaser.Radius, cfg.Gunner.Radius, Eps,
                "премисса фикстуры: у обоих кандидатов один радиус тела — иначе равенства t не будет");
            Assert.Less(d, gunnerLegs.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: ось задевает и узкие ноги ганнера — иначе он откажет и тай-брейка не будет");
            Assert.AreNotEqual(chaserCorpus.Zone, gunnerLegs.Zone,
                "премисса фикстуры: ответы двух кандидатов различимы зоной");
            Assert.Greater(cfg.Hero.MuzzleHeight, chaserCorpus.Bottom,
                "премисса фикстуры: высота дула приходится на корпус чейзера");
            Assert.Less(cfg.Hero.MuzzleHeight, gunnerLegs.Top,
                "премисса фикстуры: высота дула приходится на ноги ганнера");

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop, "ни один из двух кандидатов не остановил линию");
            Assert.AreEqual(chaserCorpus.Zone, line.Zone,
                "при точном равенстве выиграл не спрошенный первым");
        }

        [Test]
        public void InASlideTheAxisPassesLower()   // test 10, M309
        {
            // ⛔ A CHASER, NOT A GUNNER: the gunner's legs span both heights at
            // once, and a line frozen at the standing height would answer the
            // same zone in both calls -- the mutant would survive.
            SimConfig cfg = TestConfigs.OpenField();
            HitPart chaserLegs = cfg.Chaser.Parts[0];
            HitPart chaserCorpus = cfg.Chaser.Parts[1];
            var snap = Snap(in cfg, mobs: 1);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Chaser,
                Pos = new float2(8f, 0f), Hp = cfg.Chaser.MaxHp };

            // ⚠ THE BANDS ARE NOT PADDED BY THE ROUND'S RADIUS -- HitZones
            // says so in as many words ("NO PADDING BETWEEN PARTS"; the
            // forgiveness lives at the two ENDS of the stack). So these
            // premises are stated against the raw band edges: written with the
            // pad added, the first one would read 1.0 > 1.0 and be false on
            // correct code.
            Assert.Greater(cfg.Hero.MuzzleHeight, chaserLegs.Top,
                "премисса фикстуры: стоячее дуло выше ног цели");
            Assert.Less(cfg.Hero.MuzzleHeight, chaserCorpus.Top,
                "премисса фикстуры: стоячее дуло не выше корпуса цели");
            Assert.Less(cfg.Hero.SlideMuzzleHeight, chaserLegs.Top,
                "премисса фикстуры: слайдовое дуло приходится на ноги цели");
            Assert.AreNotEqual(chaserLegs.Zone, chaserCorpus.Zone,
                "премисса фикстуры: две высоты приходятся на разные зоны");

            AimLineSolution standing = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(chaserCorpus.Zone, standing.Zone, "стоя ось прошла не по корпусу");

            AimLineSolution sliding = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.SlideMuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(chaserLegs.Zone, sliding.Zone, "в слайде ось прошла не по ногам");
        }

        [Test]
        public void ASlidingCollectorDoesNotStopTheLine()   // test 11, M302
        {
            SimConfig cfg = TestConfigs.OpenField();
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            var otherPos = new float2(10f, 0f);
            var snap = Snap(in cfg, players: 2);
            snap.Players[1] = new PlayerState { Alive = true, Pos = otherPos,
                SlideTimer = cfg.Hero.SlideDuration };

            Assert.Greater(snap.Players[1].SlideTimer, 0f,
                "премисса фикстуры: чужой сборщик в слайде");
            Assert.Less(cfg.Hero.SlideProfileTop,
                cfg.Hero.MuzzleHeight - cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: силуэт слайдящего ниже луча даже с радиусом снаряда");
            Assert.Less(math.abs(otherPos.y), cfg.Hero.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: круг тела накрывает ось — мешать держать может только высота");

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreNotEqual(AimStop.Body, line.Stop,
                "слайдящий сборщик удержал линию — потолок силуэта взят не по слайду");
            Assert.AreEqual(reach, line.Length, Eps,
                "линия над слайдящим не дошла до предела дальности");
        }

        [Test]
        public void MyOwnBodyIsExcluded()   // test 12, M305
        {
            // ⛔ THE OWN BODY IS PUT ON THE RAY AHEAD OF THE MUZZLE, and the
            // fixture would prove nothing otherwise: at the fixture's numbers
            // the padded collector radius is smaller than the muzzle offset, so
            // a body left at the shooter's own feet starts the segment OUTSIDE
            // its own circle and the line never meets it -- the mutant that
            // forgets to exclude it would survive. The position of the shooter
            // is therefore passed separately from the position of his body.
            SimConfig cfg = TestConfigs.OpenField();
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            var hero = float2.zero;
            var dir = new float2(1f, 0f);
            float2 muzzle = hero + dir * cfg.Weapon.MuzzleOffset;
            float2 ownBody = hero + dir * 5f;
            var snap = Snap(in cfg);
            snap.Players[0] = new PlayerState { Alive = true, Pos = ownBody };

            Assert.Less(cfg.Hero.Radius + cfg.Weapon.ProjectileRadius, cfg.Weapon.MuzzleOffset,
                "премисса фикстуры: у своих ног тело линию не пересекло бы вовсе — падированный радиус меньше выноса дула");
            Assert.Greater(math.dot(ownBody - muzzle, dir), 0f,
                "премисса фикстуры: своё тело впереди дула, а не позади");
            Assert.Less(math.abs(ownBody.y - muzzle.y), cfg.Hero.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: своё тело падированным кругом накрывает ось линии");

            AimLineSolution line = AimLine.Solve(hero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreNotEqual(AimStop.Body, line.Stop, "линия остановилась на собственном теле");
            Assert.AreEqual(reach, line.Length, Eps,
                "линия сквозь своё тело не дошла до предела дальности");
        }

        [Test]
        public void ABarrierBelowTheMuzzleDoesNotHold()   // test 13, M303
        {
            SimConfig cfg = TestConfigs.OpenField();
            // ⚠ THE INTERVAL IS NARROWER THAN IT LOOKS: the height gate pads
            // the barrier by the round's own radius at both ends, so a top
            // between the muzzle and the muzzle minus that radius still HOLDS.
            // The fixture states a top halfway into the interval that does not.
            cfg.Arena.BarrierTop = 0.5f * (cfg.Hero.MuzzleHeight - cfg.Weapon.ProjectileRadius);
            TestConfigs.PutObstacle(ref cfg, new float2(8f, 0f), 1f);
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;

            Assert.Greater(cfg.Arena.BarrierTop, 0f,
                "премисса фикстуры: у барьера есть высота — нулевая держит всё");
            Assert.Less(cfg.Arena.BarrierTop,
                cfg.Hero.MuzzleHeight - cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: барьер ниже луча даже с радиусом снаряда");

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.AreNotEqual(AimStop.Barrier, line.Stop, "низкий барьер удержал линию");
            Assert.AreEqual(reach, line.Length, Eps,
                "линия над низким барьером не дошла до предела дальности");
        }

        [Test]
        public void CollectorsAreGatedOnAlive_MobsAreNot()   // test 14, M310
        {
            // ⛔ THE TWO HALVES BELONG IN ONE FIXTURE because they are one
            // rule read from both sides. A collector is gated on Alive and NOT
            // on health: he leaves the raid alive and well, and a line that
            // gated on health would still be stopped by a body that is no
            // longer in the arena. A mob is not gated at all: health rides the
            // wire as a byte, so a live body under 1/510 of its maximum
            // decodes to zero -- the Director's last few points -- and a gate
            // on health would blind the aim line exactly while the boss is
            // being finished off.
            SimConfig cfg = TestConfigs.OpenField();
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;

            var evacuated = Snap(in cfg, players: 2);
            evacuated.Players[1] = new PlayerState { Alive = false, Extracted = true,
                Pos = new float2(8f, 0f), Hp = cfg.Hero.MaxHp };
            Assert.Greater(evacuated.Players[1].Hp, 0f,
                "премисса фикстуры: эвакуант ушёл живым — здоровье положительно");
            Assert.IsFalse(evacuated.Players[1].Alive,
                "премисса фикстуры: эвакуанта в арене больше нет");
            AimLineSolution overGone = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, evacuated, 0, Scratch(in cfg));
            Assert.AreNotEqual(AimStop.Body, overGone.Stop,
                "эвакуант удержал линию — гейт сборщиков не по Alive");
            Assert.AreEqual(reach, overGone.Length, Eps,
                "линия сквозь ушедшего не дошла до предела дальности");

            var quantized = Snap(in cfg, mobs: 1);
            quantized.Mobs[0] = new MobState { Id = 1, Type = MobType.Director,
                Pos = new float2(8f, 0f), Hp = 0f };
            Assert.AreEqual(0f, quantized.Mobs[0].Hp, Eps,
                "премисса фикстуры: здоровье живого босса декодировано проводом в ноль");
            AimLineSolution onBoss = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, quantized, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, onBoss.Stop,
                "моб с нулевым декодированным здоровьем линию не удержал — мобы гейтятся по здоровью");
        }

        [Test]
        public void TheZoneUnderTheAxisIsReported()   // test 15
        {
            SimConfig cfg = TestConfigs.OpenField();
            HitPart gunnerLegs = cfg.Gunner.Parts[0];
            HitPart heroCorpus = cfg.Hero.Parts[1];
            var at = new float2(5f, 0f);

            Assert.Less(cfg.Hero.MuzzleHeight, gunnerLegs.Top,
                "премисса фикстуры: на высоте дула у ганнера стоят ноги");
            Assert.Greater(cfg.Hero.MuzzleHeight, heroCorpus.Bottom,
                "премисса фикстуры: на высоте дула у сборщика стоит корпус");
            Assert.Less(cfg.Hero.MuzzleHeight, heroCorpus.Top,
                "премисса фикстуры: высота дула не выше корпуса сборщика");
            Assert.AreNotEqual(gunnerLegs.Zone, heroCorpus.Zone,
                "премисса фикстуры: две цели отвечают разными зонами");

            var mobs = Snap(in cfg, mobs: 1);
            mobs.Mobs[0] = new MobState { Id = 1, Type = MobType.Gunner, Pos = at,
                Hp = cfg.Gunner.MaxHp };
            AimLineSolution onGunner = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, mobs, 0, Scratch(in cfg));
            Assert.AreEqual(gunnerLegs.Zone, onGunner.Zone, "зона на ганнере — не ноги");

            var collectors = Snap(in cfg, players: 2);
            collectors.Players[1] = new PlayerState { Alive = true, Pos = at };
            AimLineSolution onCollector = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, collectors, 0, Scratch(in cfg));
            Assert.AreEqual(heroCorpus.Zone, onCollector.Zone, "зона на сборщике — не корпус");
        }

        [Test]
        public void TheOrderIsByBodyCircle_NotByPart()   // test 19, M306
        {
            // ⛔⛔ THE WHOLE FIXTURE IS ARITHMETIC, so the arithmetic is stated
            // as PREMISES below rather than trusted. Body A is a gunner whose
            // narrow legs stand at the muzzle's height, offset sideways so that
            // the axis clips the legs by a hair while cutting deep into the
            // body circle: the two entries then lie 0.32 m apart. Body B stands
            // on the axis a little farther out, inside the window where the two
            // rankings DISAGREE -- by body circles A is nearer, by parts B is.
            // ⚠ B IS A CHASER RATHER THAN A GUNNER, and that is the one place
            // this fixture departs from the plan's letter: two gunners answer
            // the same zone, so the only observable left would be Length, whose
            // convention (the body-circle entry or the part's own contact)
            // Solve has not yet fixed -- an assertion on it could be red on
            // correct code. A chaser answers the corpus where the gunner
            // answers legs, so the winner is observable whatever that
            // convention turns out to be. The chosen gap of 0.29 m lies inside
            // the plan's own window (0.204 .. 0.374) as well as inside the
            // wider one this substitution opens (0.204 .. 0.524).
            SimConfig cfg = TestConfigs.OpenField();
            HitPart gunnerLegs = cfg.Gunner.Parts[0];
            HitPart chaserCorpus = cfg.Chaser.Parts[1];
            const float offset = 0.46f;
            const float gap = 0.29f;
            // How far A's center stands DOWN THE LINE from the muzzle: the
            // fixture's own input, and what the length expectation below is
            // measured from.
            const float toA = 8f;
            float aX = cfg.Weapon.MuzzleOffset + toA;

            float padBody = cfg.Gunner.Radius + cfg.Weapon.ProjectileRadius;
            float padPartA = gunnerLegs.Radius + cfg.Weapon.ProjectileRadius;
            float padPartB = chaserCorpus.Radius + cfg.Weapon.ProjectileRadius;
            float backCircle = math.sqrt(padBody * padBody - offset * offset);
            float backPart = math.sqrt(padPartA * padPartA - offset * offset);
            float windowLow = padBody - backCircle;
            float windowHigh = padPartB - backPart;

            Assert.Less(offset, padBody,
                "премисса фикстуры: круг тела A ось задевает");
            Assert.Less(offset, padPartA,
                "премисса фикстуры: узкую часть A ось задевает тоже — иначе A отказал бы и предмета теста нет");
            Assert.Greater(gap, windowLow,
                "премисса фикстуры: по кругам тел ближним оказывается A");
            Assert.Less(gap, windowHigh,
                "премисса фикстуры: по кругам частей ближним оказался бы B — иначе два порядка неразличимы");
            Assert.AreNotEqual(gunnerLegs.Zone, chaserCorpus.Zone,
                "премисса фикстуры: два тела отвечают разными зонами");

            var snap = Snap(in cfg, mobs: 2);
            snap.Mobs[0] = new MobState { Id = 1, Type = MobType.Gunner,
                Pos = new float2(aX, offset), Hp = cfg.Gunner.MaxHp };
            snap.Mobs[1] = new MobState { Id = 2, Type = MobType.Chaser,
                Pos = new float2(aX + gap, 0f), Hp = cfg.Chaser.MaxHp };

            AimLineSolution line = AimLine.Solve(float2.zero, new float2(30f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, snap, 0, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop, "ни одно из двух тел не остановило линию");
            Assert.AreEqual(gunnerLegs.Zone, line.Zone,
                "кандидаты ранжированы по частям, а не по кругам тел");

            // ⛔⛔ AND THE LENGTH IS PINNED HERE, BECAUSE NOTHING ELSE IN THE SET
            // PINS IT. On a body stop `Length` is the first contact with the
            // PART struck, not the entry into the body circle the candidates
            // were RANKED by -- two roles of one `t`, and the shot itself keeps
            // them apart the same way. This fixture is the only place in the
            // file where the two numbers differ at all: on a chaser they are
            // identically equal (its corpus is as wide as its body), and in the
            // seam fixture the difference sinks under a tolerance of one
            // projectile step. Without this assertion a mutant reporting the
            // circle's own `t` passes all 24 fixtures.
            // ⚠ IT DOES NOT REPLACE THE ZONE ASSERTION ABOVE: that one pins the
            // ORDER of the candidates (M306), this one pins the CONVENTION of
            // the length.
            Assert.Greater(backCircle - backPart, Eps,
                "премисса фикстуры: две конвенции длины здесь расходятся — иначе ассерт ниже ничего не свидетельствует");
            Assert.AreEqual(toA - backPart, line.Length, Eps,
                "длина луча взята по кругу тела, а не по первому контакту с задетой частью");
        }

        // ---- third group (Step 1b): the notches ----

        [Test]
        public void NotchesStandAtMinOfStopAndCursor()   // test 24, M319
        {
            // ⛔ THE EXPECTATION IS A NUMBER OF THE FIXTURE, NOT THE FORMULA.
            // Pinning min(Length, distance(Start, cursor)) would be green on
            // the constant stub -- min(0, d) is 0 -- and a tautology
            // f(x) == f(x) after GREEN, i.e. useless twice over. So the
            // cursor's own distance is stated here and the answer is measured
            // against it.
            SimConfig cfg = TestConfigs.OpenField();
            const float toCursor = 8f;
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            float expected = toCursor - cfg.Weapon.MuzzleOffset;   // measured from the muzzle
            Assert.Less(expected, reach,
                "премисса фикстуры: курсор ближе предела дальности — иначе минимум берёт упор");

            AimLineSolution open = AimLine.Solve(float2.zero, new float2(toCursor, 0f),
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));
            Assert.AreEqual(expected, open.NotchDistance, Eps,
                "засечки встали не у курсора, когда упор дальше него");

            // The other half: a wall nearer than the cursor takes the minimum
            // over, and then the notches ride the stop.
            const float wallX = 3f;
            SimConfig walled = TestConfigs.OpenField();
            walled.Arena.BarrierTop = BarrierTopThatHolds(in walled);
            TestConfigs.PutWall(ref walled, new float2(wallX, -4f), new float2(wallX, 4f), 0.5f);
            Assert.Greater(walled.Arena.BarrierTop,
                walled.Hero.MuzzleHeight + walled.Weapon.ProjectileRadius,
                "премисса фикстуры: стена выше луча и обязана держать");
            Assert.Less(wallX - walled.Weapon.MuzzleOffset, expected,
                "премисса фикстуры: стена ближе курсора");

            AimLineSolution stopped = AimLine.Solve(float2.zero, new float2(toCursor, 0f),
                walled.Hero.MuzzleHeight, in walled, Snap(in walled), 0, Scratch(in walled));
            Assert.AreEqual(AimStop.Barrier, stopped.Stop, "стена не остановила линию");
            Assert.Greater(stopped.Length, 0f, "линия встала в нуле, а не на стене");
            Assert.Less(stopped.Length, expected, "линия ушла за стену, дальше курсора");
            Assert.AreEqual(stopped.Length, stopped.NotchDistance, Eps,
                "засечки встали не на упоре, когда упор ближе курсора");
        }

        [Test]
        public void TheTwoNotchesStandOnTheConeEdges()   // test 25, M320
        {
            SimConfig cfg = TestConfigs.OpenField();
            const float stroke = 0.5f;
            AimLineSolution line = AimLine.Solve(float2.zero, new float2(8f, 0f),
                cfg.Hero.MuzzleHeight, in cfg, Snap(in cfg), 0, Scratch(in cfg));

            // ⛔ THIS PREMISE IS WHAT KEEPS THE ASSERTIONS BELOW FROM PASSING ON
            // ZEROES: at a zero half-width both strokes collapse onto the axis
            // and every distance and dot product below is trivially met.
            Assert.Greater(line.NotchHalfWidth, 0f,
                "премисса фикстуры: полуширина конуса на дистанции засечек положительна");

            AimLine.Notches(in line, stroke, out float2 a0, out float2 a1,
                out float2 b0, out float2 b1);
            var perp = new float2(-line.Dir.y, line.Dir.x);
            // ⚠ THE DISTANCE IS MEASURED AT THE MIDDLE OF EACH STROKE, and that
            // is not a nicety: the ends stand at NotchHalfWidth -/+ half the
            // stroke, so NONE of the four outputs sits at the half-width itself
            // and an assertion on any one of them would be red on correct code.
            float2 midA = 0.5f * (a0 + a1);
            float2 midB = 0.5f * (b0 + b1);
            Assert.AreEqual(line.NotchHalfWidth, math.dot(midA - line.NotchAt, perp), Eps,
                "середина первого штриха стоит не на границе конуса");
            Assert.AreEqual(-line.NotchHalfWidth, math.dot(midB - line.NotchAt, perp), Eps,
                "середина второго штриха стоит не на другой границе конуса");
            Assert.AreEqual(0f, math.dot(midA - line.NotchAt, line.Dir), Eps,
                "штрих стоит не на дистанции засечек");
            Assert.AreEqual(0f, math.dot(a1 - a0, line.Dir), Eps,
                "первый штрих отложен не поперёк линии");
            Assert.AreEqual(stroke, math.distance(a0, a1), Eps,
                "длина первого штриха не равна переданной");
            // ⚠ THE SECOND STROKE IS EXAMINED IN FULL TOO, and it is not
            // symmetry for symmetry's sake: with only its middle asserted, a
            // mutant laying THIS pair ALONG the line instead of across it
            // passes every other assertion in the fixture.
            Assert.AreEqual(0f, math.dot(b1 - b0, line.Dir), Eps,
                "второй штрих отложен не поперёк линии");
            Assert.AreEqual(stroke, math.distance(b0, b1), Eps,
                "длина второго штриха не равна переданной");
        }

        [Test]
        public void TheNotchStrokeHasAFloor()   // test 26, M322
        {
            // ⛔ THE SUBJECT IS AimLine.NotchStroke, NOT A LENGTH COMPUTED
            // INSIDE THE VIEW (plan deviation 8): a formula living in a
            // MonoBehaviour has neither a witness nor a victim for its
            // mutation, EditMode not raising one at all. The view hands over
            // two numbers out of the ScriptableObject; the decision is made
            // here.
            const float frac = 0.5f;
            const float minLength = 0.3f;
            const float narrow = 0.2f;
            const float wide = 2f;

            // Both branches in one fixture, and each with its own premise --
            // otherwise one of the two is asserted on the branch that does not
            // run.
            Assert.Less(frac * narrow, minLength,
                "премисса фикстуры: у малой полуширины доля меньше пола");
            Assert.Greater(frac * wide, minLength,
                "премисса фикстуры: у большой полуширины доля больше пола");

            Assert.AreEqual(minLength, AimLine.NotchStroke(narrow, frac, minLength), Eps,
                "на малой полуширине пол не сработал");
            Assert.AreEqual(frac * wide, AimLine.NotchStroke(wide, frac, minLength), Eps,
                "на большой полуширине длина штриха не равна доле");
        }

        // ---- the seams (Step 13): the line against a real shot ----

        [Test]
        public void TheShotLandsWhereTheLinePointed()   // test 28 -- ⭐⭐ A DoD ITEM
        {
            // ⛔ THE ONE FIXTURE THAT TIES THE LINE TO A REAL SHOT. Everything
            // above examines the pure function against a frame built by hand;
            // this one fires a round out of a live world and asks the SERVER,
            // through its own event, where it landed.
            //
            // ⚠ THE PREMISES ARE WHAT MAKE THE COMPARISON MEAN ANYTHING, and
            // each is asserted rather than assumed: the cone is zeroed (a shot
            // inside a live cone leaves the axis the line draws), the target
            // stands still for the whole flight (a chaser under its own legs
            // covers 0.173 m per tick straight at the shooter, and the freeze
            // has to be proven the way every other caller of FreezeArchetype
            // proves it), and the target really is on the line.
            //
            // ⚠ THE ORDER IS LOAD-BEARING: Solve reads the frame taken BEFORE
            // the shot, then the tick with the trigger down, then the event.
            // Any other order has the line and the round looking at different
            // ticks.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilMaxRad = 0f;
            cfg.Weapon.SprayVariance = 0f;
            cfg.Weapon.SprayPitchAmplitude = 0f;
            // ⛔ BEFORE `new SimulationWorld`, which is the whole of the freeze:
            // setting the mob's Ai to Idle does NOT stand it still, MobAiSystem
            // puts it back into Chase on the very next tick.
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);

            var w = new SimulationWorld(seed: 1, cfg);
            var targetPos = new float2(8f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, targetPos));
            int targetId = w.Mobs[0].Id;
            SimInput fire = TestWorlds.HipFire();

            // The frame the picture is drawn from -- the STANDARD capture, the
            // same one ten test files take, and LocalPlayerIndex is the
            // fixture's to state because the method deliberately never touches
            // it.
            var snap = new RenderSnapshot(in cfg);
            w.CaptureSnapshot(snap);
            snap.LocalPlayerIndex = 0;
            PlayerState shooter = snap.Player;

            Assert.AreEqual(0f, Spread.HipRadians(in cfg.Weapon, in shooter, in cfg.Hero), Eps,
                "премисса фикстуры: конус занулён — иначе выстрел уходит внутри конуса, а линия рисует ось");
            Assert.Less(math.abs(targetPos.y - shooter.Pos.y), cfg.Chaser.Radius,
                "премисса фикстуры: цель стоит на линии огня");

            AimLineSolution line = AimLine.Solve(shooter.Pos, fire.AimPoint,
                cfg.Hero.MuzzleHeight, in cfg, snap, snap.LocalPlayerIndex, Scratch(in cfg));
            Assert.AreEqual(AimStop.Body, line.Stop,
                "премисса фикстуры: линия показывает не на тело — сравнивать нечего");

            w.Tick(fire);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent hit),
                "выстрела в тело не случилось вовсе");
            Assert.AreEqual(0f, math.distance(targetPos, w.Mobs[0].Pos), Eps,
                "премисса фикстуры: цель сдвинулась за время полёта — линия и выстрел смотрели бы на разные позиции");

            // ⚠ THE BODY AND THE ZONE STRICTLY, THE POINT WITHIN ONE STEP OF
            // THE ROUND: the shot resolves inside the step that meets the body,
            // so the picture cannot be held to a finer distance than the step
            // this fixture's own weapon takes in a tick.
            Assert.AreEqual(targetId, hit.EntityId,
                "выстрел пришёл не в то тело, на которое показывала линия");
            Assert.AreEqual(line.Zone, hit.Zone, "зона выстрела разошлась с зоной линии");
            Assert.AreEqual(0f, math.distance(line.End, hit.Pos),
                cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt,
                "точка попадания разошлась с концом луча больше чем на шаг снаряда");
        }

        [Test]
        public void ThePicturesMuzzleIsTheShotsMuzzle()   // test 29 -- ⭐ A DoD ITEM
        {
            // The witness that the copies of the muzzle formula have not
            // drifted apart. ⛔ THERE ARE THREE OF THEM: both branches of
            // ShotGeometry.Solve and SimulationRunner.RenderMuzzleSimPos, which
            // T2 replaces with a call to the one this file owns. Both branches
            // of the shot are run here, because the aimed one builds the muzzle
            // out of a 3D expression of its own.
            //
            // ⛔⛔ NO WORLD IS NEEDED, AND A LIVE SHOT WOULD MAKE THIS FIXTURE
            // RED ON CORRECT CODE: ShotGeometry.Solve is public and takes
            // `overshoot` as a parameter, so both branches are called directly
            // with zero. Fired through a fresh world instead, the weapon's
            // cooldown stands at zero, the tick subtracts dt from it, and the
            // shot is handed an overshoot of one tick -- which walks the spawn
            // point 35/30 = 1.167 m down the line, nearly twice the muzzle
            // offset itself.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilMaxRad = 0f;
            var p = new PlayerState { Alive = true, Pos = new float2(3f, -2f) };
            var aimPoint = new float2(13f, -2f);
            var hipInput = new SimInput { AimPoint = aimPoint, FireHeld = true };
            var aimedInput = new SimInput
            {
                AimPoint = aimPoint,
                // ⚠ AT THE MUZZLE'S OWN HEIGHT, or the aimed branch aims into
                // the floor and answers a different question.
                AimHeight = cfg.Hero.MuzzleHeight,
                AimHeld = true,
                FireHeld = true,
            };

            // ⛔ THE PREMISE IS EXPRESSED BY A CALL, one for each branch's own
            // half of the cone: with a live cone the shot's direction is drawn
            // AFTER the spray and cannot coincide with the picture's by
            // construction.
            Assert.AreEqual(0f, Spread.HipRadians(in cfg.Weapon, in p, in cfg.Hero), Eps,
                "премисса фикстуры: бедровый конус занулён");
            Assert.AreEqual(0f, Spread.AimRadians(in cfg.Weapon, in p, in cfg.Hero), Eps,
                "премисса фикстуры: прицельный конус занулён");
            // And without a muzzle offset both sides would be the collector's
            // own position and the fixture would hold for the wrong reason.
            Assert.Greater(cfg.Weapon.MuzzleOffset, 0f,
                "премисса фикстуры: дуло вынесено вперёд от сборщика");

            float2 expected = AimLine.MuzzleSimPos(p.Pos, aimPoint, cfg.Weapon.MuzzleOffset,
                out _);
            ShotSolution hip = ShotGeometry.Solve(in p, in hipInput, in cfg, 0f);
            ShotSolution aimed = ShotGeometry.Solve(in p, in aimedInput, in cfg, 0f);

            Assert.AreEqual(0f, math.distance(expected, hip.SpawnPos), Eps,
                "дуло картинки разошлось с бедровой веткой выстрела");
            Assert.AreEqual(0f, math.distance(expected, aimed.SpawnPos), Eps,
                "дуло картинки разошлось с прицельной веткой выстрела");
        }
    }
}
