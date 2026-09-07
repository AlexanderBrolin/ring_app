using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Presentation.Net;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Task 6: vertical hit zones, per-zone damage multipliers and the zone/
    /// direction payload the resulting events carry.
    public class HitZoneTests
    {
        /// Open arena with a frozen shooting range: these fixtures measure the
        /// vertical maths, so a mob that closes distance (or a gunner orbiting at
        /// StrafeSpeed) would move the sweep entry the expected zone is read at.
        /// Spread/recoil go to zero for the same reason.
        static SimConfig Range()
        {
            var c = TestConfigs.OpenField();
            c.Weapon.SpreadRad = 0f;
            c.Weapon.RecoilPerShotRad = 0f;
            c.Chaser.MaxSpeed = 0f;
            c.Gunner.MaxSpeed = 0f;
            c.Gunner.StrafeSpeed = 0f;
            return c;
        }

        /// Point-blank target distance: 1 m is under one tick of projectile
        /// travel (ProjectileSpeed / 30), so a shot fired here lands on the very
        /// next tick and the fixture never has to budget for flight time.
        const float TargetX = 1f;

        /// app-8dv / app-dw0z: the FAR target, for the one fixture that needs a
        /// round to still be in the air on a later tick than the one it was
        /// fired on. Not asserted as a tick count here -- the flight is a
        /// PROPERTY the fixture checks for itself (`ProjectileCount > 0` after a
        /// tick), so a change to ProjectileSpeed moves the number of ticks and
        /// breaks nothing this file claims.
        const float FlightX = 20f;

        static SimEvent Blow(SimulationWorld w, SimEventKind kind)
        {
            Assert.IsTrue(TestEvents.TryFirstOf(w, kind, out SimEvent found), $"no {kind} emitted");
            return found;
        }

        /// app-88jb T14: the middle of a body's HEAD, read off the body's own
        /// ORDERED STACK OF PARTS instead of off the vertical zone column the
        /// stack replaced (T15 deleted that column outright). Written once here
        /// because three fixtures below need exactly this expression, and three
        /// copies of it is the shape rule 2 removes.
        ///
        /// WHY IT IS NOT THE MIDPOINT OF THE COLUMN'S TOP TWO BOUNDS ANY MORE.
        /// That was the same aim point expressed through the column, and until
        /// T14 the two agreed. They do not agree now, and the difference is the whole task:
        /// the column stopped at the chaser's 1.85 m while the model stands
        /// 2.70 m tall (measured, session 43), so `0.5 * (1.45 + 1.85)` = 1.65
        /// lands squarely in the TORSO part [0.88, 2.12) and the fixture would
        /// be asking for a headshot while shooting the chest. Read off the part,
        /// the aim is 2.41 m for a chaser and 3.72 m for a gunner -- inside the
        /// head belts [2.12, 2.70] and [3.24, 4.20] the bodies actually carry.
        static float HeadBandOf(HitPart[] parts) => MidOf(parts[parts.Length - 1]);

        /// The middle of ANY part's belt. `HeadBandOf` above is this expression
        /// applied to the crown; app-8dv gave the torso a second call site, and
        /// two copies of one expression is the shape rule 2 removes -- the same
        /// reason the head version was lifted here in the first place.
        static float MidOf(HitPart part) => 0.5f * (part.Bottom + part.Top);

        [Test]
        public void Overlaps_AcceptsInsideTheRadiusPaddedColumn_RejectsOutside()
        {
            var c = TestConfigs.Default().Chaser;
            const float r = 0.12f;
            // app-88jb Т15: THE CEILING IS THE BODY'S CROWN, read off its stack
            // of parts. It was the chaser's zone-column top, and that was a
            // FIXTURE NUMBER rather than this test's subject — every assertion below is
            // expressed RELATIVE to the ceiling, so the crown moving from 1.85
            // to 2.70 moves nothing the test claims.
            float top = HitZones.StackTop(c.Parts);
            HitPart torso = c.Parts[c.Parts.Length - 2];
            float bodyHeight = MidOf(torso);
            // a flat pass at body height
            Assert.IsTrue(HitZones.Overlaps(bodyHeight, bodyHeight, r, top));
            // grazing the crown / scraping the ground: the projectile's own
            // radius extends the column by r at both ends
            Assert.IsTrue(HitZones.Overlaps(top + r - 1e-4f, top + r - 1e-4f, r, top));
            Assert.IsTrue(HitZones.Overlaps(-r + 1e-4f, -r + 1e-4f, r, top));
            Assert.IsFalse(HitZones.Overlaps(top + r + 1e-3f, top + r + 1e-3f, r, top));
            Assert.IsFalse(HitZones.Overlaps(-r - 1e-3f, -r - 1e-3f, r, top));
            // a descending shot that only clips the column on part of the chord
            // still counts — the test is interval-vs-interval, not point-vs-interval
            Assert.IsTrue(HitZones.Overlaps(top + 5f, bodyHeight, r, top));
        }

        // ── app-88jb Т14 fix-round (Rulings 191/192): DIRECT tests of
        // HitZones.Resolve. The class is internal on purpose and this assembly
        // already calls Overlaps/StackTop directly; until this round the
        // resolver itself was exercised only through world shots, which cannot
        // reach the axes below (lesson 620). ──

        [Test]
        public void Resolve_ContactHeight_LiesInsideTheWinnersBand()
        {
            // Ruling 191 (review finding A-1), the DIRECT half of the witness
            // pair -- the world half lives in HitPartsTests. One long climbing
            // chord past the Director's flank, wide of his legs and inside his
            // torso circle: the torso circle is entered while the height is
            // still below the torso band, and the first point where circle and
            // band hold AT ONCE is where the raw height crosses the band's
            // bottom. A resolver that reports the circle entry instead hands
            // back a contact height in a belt the winning part does not
            // occupy -- and that number is the moment arm and the spark point.
            SimConfig cfg = TestConfigs.Default();
            HitPart[] parts = cfg.Director.Parts;
            HitPart legs = parts[0];
            HitPart torso = parts[1];
            float projR = cfg.Weapon.ProjectileRadius;
            var p0 = float2.zero;
            var p1 = new float2(10f, 0f);
            var target = new float2(6.3f, 2f);       // 2 m off the chord's line
            const float hStart = 0.5f, hEnd = 2f;    // climbs 0.15 m per meter

            // Premises, asserted as geometry: the lateral miss lies BETWEEN
            // the legs and torso circles (only the torso is in play), and the
            // torso circle is entered below its own band while the crossing
            // of that band still happens inside the circle.
            float lateral = math.abs(target.y);
            Assert.Greater(lateral, legs.Radius + projR,
                "луч задевает круг ног — тест перестал быть о единственном кандидате");
            Assert.Less(lateral, torso.Radius + projR,
                "луч не входит в круг корпуса — кандидатов нет вовсе");
            float torsoPad = torso.Radius + projR;
            float half = math.sqrt(torsoPad * torsoPad - lateral * lateral);
            float sIn = target.x - half;
            float hIn = hStart + (hEnd - hStart) * sIn / p1.x;
            Assert.Less(hIn, torso.Bottom,
                "вход в круг корпуса лежит уже в его полосе — окно дефекта закрыто");
            float sCross = p1.x * (torso.Bottom - hStart) / (hEnd - hStart);
            Assert.Less(sCross, target.x + half,
                "полоса корпуса достигается уже вне его круга — контакта нет и тест не о высоте");

            bool resolved = HitZones.Resolve(parts, p0, p1, projR, target, hStart, hEnd,
                HitZones.StackTop(parts), out HitZone zone, out float mult,
                out float hitHeight, out float t);

            Assert.IsTrue(resolved, "единственный кандидат с пересечённой полосой не разрешён в попадание");
            Assert.AreEqual(torso.Zone, zone, "зона не корпусная, хотя пересечён только его круг");
            Assert.That(hitHeight, Is.InRange(torso.Bottom, torso.Top),
                "высота контакта вне полосы победителя — контакт объявлен там, где части нет");
            Assert.AreEqual((torso.Bottom - hStart) / (hEnd - hStart), t, 1e-5f,
                "t контакта не совпадает с первым одновременным попаданием в круг и полосу");
            Assert.AreEqual(torso.DamageMult, mult, 1e-6f, "множитель не от победителя");
        }

        [Test]
        public void Resolve_TieOfEqualEntries_KeepsTheLowerPart()
        {
            // Ruling 192 (review finding A-3): the REAL source of a tie is the
            // clamp `tEnter = max(root, 0)` in Geometry.SegmentCircleInterval
            // -- a step that BEGINS inside two parts' circles yields zero for
            // both (no body carries equal radii, so equal radii produce no
            // ties in this game). A guard, not a witness (lesson 427): green
            // on today's code and on the fixed one; what it pins is the strict
            // `<`, which keeps the LOWER part on an exact tie.
            //
            // The chord starts inside both circles and DESCENDS through the
            // legs/torso seam, so both parts pass their gates and both first
            // contacts sit at t = 0 -- the seam height is inside both CLOSED
            // bands at the step's very start.
            SimConfig cfg = TestConfigs.Default();
            HitPart[] parts = cfg.Chaser.Parts;
            HitPart legs = parts[0];
            HitPart torso = parts[1];
            float projR = cfg.Weapon.ProjectileRadius;
            var target = float2.zero;
            var p0 = new float2(0.2f, 0f);
            var p1 = new float2(-0.3f, 0f);
            float seam = legs.Top;                 // == torso.Bottom, builder rule 2
            float hStart = seam;
            float hEnd = seam - 0.1f;              // descending across the seam

            // Premise of the tie: the whole step lies inside BOTH circles, so
            // the interval clamp answers zero for both parts.
            Assert.Less(math.length(p0 - target), legs.Radius + projR,
                "старт шага вне круга ног — тай нулевых входов не воспроизводится");
            Assert.Less(math.length(p1 - target), legs.Radius + projR,
                "конец шага вне круга ног — шаг перестал целиком лежать внутри кругов");
            Assert.AreEqual(torso.Bottom, legs.Top,
                "полосы ног и корпуса не смежны — фикстура не о шве");

            bool resolved = HitZones.Resolve(parts, p0, p1, projR, target, hStart, hEnd,
                HitZones.StackTop(parts), out HitZone zone, out _, out _, out float t);

            Assert.IsTrue(resolved, "шаг внутри обоих кругов не разрешён в попадание");
            Assert.AreEqual(legs.Zone, zone, "при тае равных входов победила не нижняя часть");
            Assert.AreEqual(0f, t, 1e-6f, "тай нулевых входов дал ненулевой t контакта");
        }

        [Test]
        public void Resolve_EmptyOrAbsentStack_RefusesTheHit()
        {
            // Ruling 192: a body that declares no hit volume presents nothing
            // to hit. Unreachable through SimConfigBuilder (its own rule
            // refuses an empty stack) -- what this guards is the hand-built
            // fixture, for which a miss is the honest answer rather than an
            // exception.
            SimConfig cfg = TestConfigs.Default();
            float projR = cfg.Weapon.ProjectileRadius;
            var p0 = float2.zero;
            var p1 = new float2(2f, 0f);
            var target = new float2(1f, 0f);

            Assert.IsFalse(HitZones.Resolve(System.Array.Empty<HitPart>(), p0, p1, projR,
                    target, 1f, 1f, 2.7f, out HitZone zone, out _, out _, out _),
                "пустой стек частей разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "пустой стек вернул небезразличную зону");
            Assert.IsFalse(HitZones.Resolve(null, p0, p1, projR,
                    target, 1f, 1f, 2.7f, out zone, out _, out _, out _),
                "отсутствующий стек частей разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "отсутствующий стек вернул небезразличную зону");
        }

        [Test]
        public void StackTop_OfNullOrEmptyStack_IsZero()
        {
            // Ruling 192: the crown of a body that has no parts is zero, not a
            // NullReferenceException on the hot path -- StackTop's own
            // contract, until now exercised by no test on either arm.
            Assert.AreEqual(0f, HitZones.StackTop(null), "крона отсутствующего стека не ноль");
            Assert.AreEqual(0f, HitZones.StackTop(System.Array.Empty<HitPart>()),
                "крона пустого стека не ноль");
        }

        [Test]
        public void GunnerHeadshot_IsOneshot()
        {
            var cfg = Range();
            // app-88jb T14: the multiplier premise is read off the HEAD PART,
            // the same place the hit itself is now resolved from -- one source
            // for the aim and for the number it is expected to produce.
            HitPart gunnerHead = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
            // the balance premise this fixture rests on, asserted not assumed
            Assert.GreaterOrEqual(cfg.Weapon.Damage * gunnerHead.DamageMult, cfg.Gunner.MaxHp);

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(TargetX, 0f)));
            float headBand = HeadBandOf(cfg.Gunner.Parts);
            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);

            w.ClearEvents();
            w.Tick(default);

            Assert.AreEqual(0, w.MobCount);
            SimEvent died = Blow(w, SimEventKind.MobDied);
            Assert.AreEqual(HitZone.Head, died.Zone);
            Assert.AreEqual(1, w.Stats.Kills);
            Assert.AreEqual(1, w.Stats.HeadshotKills);
        }

        [Test]
        public void ChaserHeadshot_TwoShots()
        {
            var cfg = Range();
            // app-88jb T14: off the head PART, as GunnerHeadshot_IsOneshot above.
            HitPart chaserHead = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            float headshot = cfg.Weapon.Damage * chaserHead.DamageMult;
            Assert.Less(headshot, cfg.Chaser.MaxHp);                 // one is not enough
            Assert.GreaterOrEqual(2f * headshot, cfg.Chaser.MaxHp);   // two are

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));
            float headBand = HeadBandOf(cfg.Chaser.Parts);

            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);
            w.Tick(default);
            Assert.AreEqual(1, w.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp - headshot, w.Mobs[0].Hp, 1e-4f);

            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);
            w.ClearEvents();
            w.Tick(default);
            Assert.AreEqual(0, w.MobCount);
            Assert.AreEqual(HitZone.Head, Blow(w, SimEventKind.MobDied).Zone);
            Assert.AreEqual(1, w.Stats.HeadshotKills); // one kill, counted once
        }

        [Test]
        public void LegsHit_AmountIsLegsMult()
        {
            var cfg = Range();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));
            // app-88jb Т15: aim AND expectation both off the legs PART, the
            // same source the blow is resolved from since Т14.
            HitPart chaserLegs = cfg.Chaser.Parts[0];
            float legsBand = 0.5f * chaserLegs.Top;
            TestWorlds.FireAimed3D(w, float2.zero, legsBand, new float2(TargetX, 0f), legsBand);

            w.ClearEvents();
            w.Tick(default);

            SimEvent hit = Blow(w, SimEventKind.ProjectileHit);
            Assert.AreEqual(HitZone.Legs, hit.Zone);
            float expected = cfg.Weapon.Damage * chaserLegs.DamageMult;
            Assert.AreEqual(expected, hit.Amount, 1e-4f);
            Assert.AreEqual(cfg.Chaser.MaxHp - expected, w.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void Fist_ZoneBody_NoMult()
        {
            var cfg = Range();
            // a deliberately non-neutral Body multiplier on the collector: if
            // the telegraphed strike ever routed through the per-part table,
            // the damage asserted below would come out doubled. app-88jb Т15:
            // set on the PART, the only place a multiplier lives now.
            for (int i = 0; i < cfg.Hero.Parts.Length; i++)
                if (cfg.Hero.Parts[i].Zone == HitZone.Body) cfg.Hero.Parts[i].DamageMult = 2f;
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));

            SimEvent damaged = default;
            bool struck = false;
            // Idle -> Chase -> Telegraph -> strike after TelegraphSeconds
            int budget = 4 + (int)math.ceil(cfg.Chaser.TelegraphSeconds / SimulationWorld.TickDt);
            for (int i = 0; i < budget && !struck; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                struck = TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out damaged);
            }

            Assert.IsTrue(struck, "the chaser never landed its telegraphed strike");
            Assert.AreEqual(HitZone.Body, damaged.Zone);
            Assert.AreEqual(cfg.Chaser.ContactDamage, damaged.Amount, 1e-4f);
            // attacker -> victim: the chaser sits at +X, the player at the origin
            Assert.AreEqual(-1f, damaged.HitDir.x, 1e-4f);
            Assert.AreEqual(0f, damaged.HitDir.y, 1e-4f);
        }

        [Test]
        public void Hit_Amount_IsPostMultiplier()
        {
            var cfg = Range();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));
            // app-88jb T14: aim AND expectation both off the head PART.
            HitPart chaserHead = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            float headBand = HeadBandOf(cfg.Chaser.Parts);
            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);

            w.ClearEvents();
            w.Tick(default);

            SimEvent hit = Blow(w, SimEventKind.ProjectileHit);
            Assert.AreEqual(HitZone.Head, hit.Zone);
            Assert.AreNotEqual(cfg.Weapon.Damage, hit.Amount); // NOT the projectile's base damage
            Assert.AreEqual(cfg.Weapon.Damage * chaserHead.DamageMult, hit.Amount, 1e-4f);
            // and the Hp the mob actually lost is that same post-multiplier number
            Assert.AreEqual(cfg.Chaser.MaxHp - hit.Amount, w.Mobs[0].Hp, 1e-4f);
            Assert.AreEqual(1f, hit.HitDir.x, 1e-4f); // travelling +X
        }

        [Test]
        public void GunnerShot_MissesSlidingHero()
        {
            var cfg = Range();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            // QA1 seam: force mid-slide directly — no need to choreograph a
            // real run-up/slide-request just to get SlideTimer > 0.
            p.SlideTimer = cfg.Hero.SlideDuration;
            w.SetPlayerForTest(p);

            // Horizontal shot at the Gunner's muzzle height (M13): 0.55
            // (SlideProfileTop) + 0.15 (Gunner.ProjectileRadius) < 0.95
            // (Gunner.MuzzleHeight) — the sliding profile must let it pass clean over.
            // Stage 2 Task 10: ownerIndex is explicit here — the seam's default
            // is 0 (a solo PLAYER's shot), while a real mob shot always carries
            // ProjectileIds.NoOwner. Harmless while OwnerIndex sat outside the
            // hash; it is inside it from this task on, so the fixture has to model
            // the production value it claims to (carryover-t10.md item 2).
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(TargetX, 0f),
                new float2(-cfg.Gunner.ProjectileSpeed, 0f), cfg.Gunner.MuzzleHeight, 0f,
                cfg.Gunner.ProjectileDamage, cfg.Gunner.ProjectileRadius, cfg.Gunner.ProjectileLifetime,
                ownerIndex: ProjectileIds.NoOwner);

            w.ClearEvents();
            w.Tick(default);

            Assert.AreEqual(cfg.Hero.MaxHp, w.Player.Hp, "sliding profile must have let the shot pass clean over");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.PlayerDamaged));
        }

        [Test]
        public void SlidingHero_HitOnlyBelowProfile()
        {
            var cfg = Range();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration; // QA1 seam
            w.SetPlayerForTest(p);

            // below SlideProfileTop (0.55) and the top of his legs part (0.55)
            const float shotHeight = 0.3f;
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(TargetX, 0f),
                new float2(-cfg.Gunner.ProjectileSpeed, 0f), shotHeight, 0f,
                cfg.Gunner.ProjectileDamage, cfg.Gunner.ProjectileRadius, cfg.Gunner.ProjectileLifetime,
                ownerIndex: ProjectileIds.NoOwner); // Stage 2 Task 10: see the sibling fixture above

            w.ClearEvents();
            // app-88jb T14: THE ROUND IS FLOWN TO ITS END INSTEAD OF BEING
            // JUDGED AFTER EXACTLY ONE TICK, and the reason is arithmetic
            // rather than taste. A NARROW PART IS ENTERED LATER THAN THE BODY,
            // by (body radius - part radius) along the ray: this collector's
            // legs are 0.32 wide against a body of 0.45, so with the round's
            // own 0.15 the two circles are entered at x = 0.47 and x = 0.60.
            // A gunner's round covers 14 / 30 = 0.4667 m per tick from
            // TargetX = 1 m, so tick one spans x: 1.0 -> 0.5333 -- it reaches
            // the BODY circle and stops short of the LEGS one, which is the
            // part this shot is actually aimed at. Under the column that
            // distinction did not exist (one half-width for the whole body) and
            // one tick was enough; under parts the blow simply lands on tick
            // two. WHAT THE TEST CLAIMS IS UNCHANGED -- a sliding collector is
            // hit BELOW his profile and the blow reads Legs -- and no
            // expectation was relaxed to make it pass: had the round missed
            // outright, RunUntilProjectilesDie would fly it past and the
            // PlayerDamaged lookup below would fail exactly as it should.
            TestWorlds.RunUntilProjectilesDie(w);

            SimEvent damaged = Blow(w, SimEventKind.PlayerDamaged);
            Assert.AreEqual(HitZone.Legs, damaged.Zone);
            Assert.Less(w.Player.Hp, cfg.Hero.MaxHp);
        }

        // -- app-8dv / app-dw0z: the per-zone hit instrument. The counters
        // measure HITS; HeadshotKills, which the epic already had, measures a
        // subset of KILLS. Conflating the two is the defect this task exists to
        // remove (lesson 687). --

        [Test]
        public void AHeadHitRaisesHeadHits_ButNotHeadshotKills_WhenTheMobSurvives()   // test 30
        {
            // M261's witness. The two counters told apart directly: a head hit
            // on a mob that SURVIVED. Today the statistics know nothing at all
            // about this shot -- HeadshotKills stays 0 because nothing died,
            // and there was no other place for the hit to be recorded.
            var cfg = Range();
            HitPart chaserHead = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            // The balance premise this fixture rests on, asserted as a PROPERTY
            // rather than trusted: one head hit must leave this mob alive, or
            // the test would be measuring a kill and not a hit.
            Assert.Less(cfg.Weapon.Damage * chaserHead.DamageMult, cfg.Chaser.MaxHp,
                "премисса: один хедшот обязан оставить чейзера живым");

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));
            float headBand = HeadBandOf(cfg.Chaser.Parts);
            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);

            w.ClearEvents();
            w.Tick(default);

            Assert.AreEqual(HitZone.Head, Blow(w, SimEventKind.ProjectileHit).Zone,
                "премисса: выстрел обязан разрешиться в голову");
            Assert.AreEqual(1, w.MobCount, "премисса: моб обязан пережить попадание");
            Assert.AreEqual(1, w.StatsAt(0).HeadHits, "попадание в голову не сосчитано");
            Assert.AreEqual(0, w.StatsAt(0).HeadshotKills,
                "добивания не было — счётчик убийств молчит");
        }

        [Test]
        public void HeadshotKills_NeverExceedHeadHits()   // test 31, M267 -- an invariant
        {
            // No finishing blow to the head can fail to be a hit to the head.
            // ⛔ THE FIXTURE MUST CONTAIN A HEADSHOT KILL, or `0 <= X` holds on
            // every implementation and M267 outlives its own witness. The
            // shortest scenario is the gunner, who dies to a single head hit.
            var cfg = Range();
            HitPart gunnerHead = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
            Assert.GreaterOrEqual(cfg.Weapon.Damage * gunnerHead.DamageMult, cfg.Gunner.MaxHp,
                "премисса: хедшот обязан убивать ганнера с одного выстрела");

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(TargetX, 0f)));
            float headBand = HeadBandOf(cfg.Gunner.Parts);
            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);

            w.ClearEvents();
            w.Tick(default);

            // The premise that makes the invariant below non-vacuous, and it is
            // asserted rather than assumed: without a real headshot kill on the
            // board the comparison is 0 <= 0 and witnesses nothing.
            Assert.AreEqual(0, w.MobCount, "премисса: ганнер обязан умереть");
            Assert.AreEqual(1, w.StatsAt(0).HeadshotKills,
                "премисса: добивание в голову обязано быть сосчитано");
            Assert.LessOrEqual(w.StatsAt(0).HeadshotKills, w.StatsAt(0).HeadHits,
                "добиваний в голову больше, чем попаданий в голову");
        }

        [Test]
        public void TheThreeZonesSumToShotsHit_IncludingAShooterWhoDiedInFlight()   // test 32, M262
        {
            // ⭐ THE MAIN ARGUMENT FOR INCREMENTING INSIDE IncrementShotsHit:
            // the `if (_players[index].Alive)` guard already lives there, and a
            // counter lifted out of it would part company with ShotsHit at
            // exactly one shooter -- the one who died while his round was still
            // in the air.
            // ⛔⛔ THE FIXTURE IS LOAD-BEARING AND ITS CONDITION IS A PROPERTY,
            // NOT A LITERAL: the shooter MUST die mid-flight, or the sum
            // balances on the mutant too. Two shots, therefore: the first lands
            // while he is alive (so the sum is over something), the second while
            // he is dead (so the guard is what the sum is testing).
            var cfg = Range();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(FlightX, 0f)));
            HitPart chaserTorso = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 2];
            float bodyBand = MidOf(chaserTorso);
            // Premise: the mob has to outlive BOTH rounds, or the second one has
            // no target left to land on.
            Assert.Less(2f * cfg.Weapon.Damage * chaserTorso.DamageMult, cfg.Chaser.MaxHp,
                "премисса: два попадания в корпус обязаны оставить чейзера живым");

            // Round one, fired and landed by a living shooter.
            TestWorlds.FireAimed3D(w, float2.zero, bodyBand, new float2(FlightX, 0f), bodyBand);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.AreEqual(1, w.StatsAt(0).ShotsHit,
                "премисса: первый выстрел обязан попасть при живом стрелке");

            // Round two: fired, then the shooter dies while it is still flying.
            float hpBeforeSecond = w.Mobs[0].Hp;
            TestWorlds.FireAimed3D(w, float2.zero, bodyBand, new float2(FlightX, 0f), bodyBand);
            w.Tick(default);
            Assert.Greater(w.ProjectileCount, 0,
                "премисса: снаряд обязан быть ещё в полёте, иначе стрелок умрёт после попадания");
            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.Player.Alive, "премисса: стрелок обязан быть мёртв до попадания");
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.Less(w.Mobs[0].Hp, hpBeforeSecond,
                "премисса: посмертный выстрел обязан долететь и попасть");
            Assert.AreEqual(1, w.StatsAt(0).ShotsHit,
                "премисса: попадание мёртвого стрелка не засчитывается в ShotsHit");

            var s = w.StatsAt(0);
            Assert.AreEqual(s.ShotsHit, s.HeadHits + s.BodyHits + s.LegHits,
                "сумма зон разошлась со ShotsHit — счётчик вынесен из-под гарда Alive");
        }

        [Test]
        public void TheZoneCountersRideTheEndOfMatchMessage()   // test 33, M268
        {
            // The counters have to survive the WIRE, not merely exist in the
            // simulation: on the networked backend HasMatchStats is false, so
            // nothing a client shows comes off the live world.
            // ⚠ THIS PINS THE TRANSPORT, NOT A SCREEN, and the difference is
            // worth stating because the two are easy to conflate. The numbers
            // do reach MatchStats through FinalStats.PersonalFrom -- which is
            // exactly what this test measures -- but the results screen
            // (DeathOverlayController.BuildMetricsText) prints six of that
            // struct's thirteen fields and no zone is among them. At the
            // milestone these counters are read from the SERVER LOG
            // (MatchSummaryLog.PlayerLine); whether the screen should grow
            // three more rows is the owner's call, not this task's.
            var ended = new MatchEndedNet { HeadHits = 5, BodyHits = 7, LegHits = 3 };
            MatchStats personal = FinalStats.PersonalFrom(in ended);
            Assert.AreEqual(5, personal.HeadHits, "попадания в голову не доехали до итогов матча");
            Assert.AreEqual(7, personal.BodyHits, "попадания в корпус не доехали до итогов матча");
            Assert.AreEqual(3, personal.LegHits, "попадания по ногам не доехали до итогов матча");
        }
    }
}
