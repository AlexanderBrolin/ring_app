using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;   // float2, for the Т14 additions this class receives

namespace Ring.Simulation.Tests
{
    /// app-88jb Т13 (spec §3.3/§3.10): the body as an ORDERED STACK OF PARTS,
    /// and the rules that keep such a stack meaningful. Five of the six tests
    /// below are validation witnesses — each drives ONE rule off the shipped
    /// configuration through ConfigTests.BuildShipped, so a rule is exercised
    /// against what the game really carries rather than against an all-zero
    /// stand-in (BuildShipped's own doc records why that distinction cost this
    /// project a silently-weakened rule once already).
    ///
    /// THE SIXTH IS A GUARD, NOT A WITNESS (lesson 427), and it is named as one
    /// in its own doc: the head's share of the column is already inside the
    /// genre band on today's numbers, so it is green before this task changes a
    /// single height. What it guards is the direction of the change — v1 of this
    /// geometry raised one Top and left the rest, which put the head at 36-46 %
    /// of the body and turned a shot to the chest into a headshot.
    ///
    /// app-88jb T14 ADDS EIGHT MORE, AND THEY ARE OF A DIFFERENT KIND: they go
    /// THROUGH THE WORLD (TestWorlds.FireAimed3D + RunUntilProjectilesDie),
    /// because what they witness is that ProjectileSystem.AcceptCandidate
    /// resolves a blow onto a PART -- which part, at what height and with
    /// whose multiplier -- and no validation of the data can show that. Seven
    /// of them stand on TestConfigs.Open(): they fire from float2.zero with an
    /// explicit origin and never relocate the collector, so he stays on the
    /// spawn ring 159.16 m out, the core is never occupied and the Director is
    /// never woken (the same boundary ImpactKnockbackTests' own header draws).
    /// The eighth, SlidingCollector_IsMissedByAShotOnTheGunnerMuzzleLine,
    /// DOES move a collector into the core and therefore takes OpenField()
    /// instead -- see its own doc for the structural reason.
    public class HitPartsTests
    {
        [Test]
        public void Validate_PartWiderThanTheBody_Throws()
        {
            // Rule 4 — the most expensive one: a part wider than its body would
            // drop out of the candidate gather SILENTLY, and the only thing that
            // would ever show it is a playtest (findings B-I6/D-I2).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Radius = g.Radius + 0.01f;          // the SECOND part
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts[1].Radius"));
            Assert.That(ex.Message, Does.Contain("must not exceed"));
        }

        [Test]
        public void Validate_GapBetweenParts_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Bottom = g.Parts[0].Top + 0.1f;     // a gap between 0 and 1
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts"));
            Assert.That(ex.Message, Does.Contain("contiguous"));
        }

        [Test]
        public void Validate_DuplicateZone_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].Zone = g.Parts[0].Zone;             // two sets of "legs"
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("appears twice"));
        }

        [Test]
        public void Validate_SlideProfileOffAnyPartBoundary_Throws()
        {
            // Rule 5: the slide profile is obliged to COINCIDE with a part
            // boundary, otherwise equivalence with today's behavior is held
            // together by the data happening to agree rather than by a rule
            // (finding C-M3).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.SlideProfileTop = 0.61f;                     // past every boundary
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.SlideProfileTop"));
            Assert.That(ex.Message, Does.Contain("part boundary"));
        }

        [Test]
        public void Validate_MaxAimHeightBelowTheDirectorsCrown_Throws()
        {
            // Rule 14 grows to FOUR archetypes: today the Director takes no part
            // in it, and his head would be unreachable by any aim at all
            // (finding C-I1).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var (elite, director) = ConfigTests.MakeShippedArchetypes();
            h.MaxAimHeight = director.Parts[director.Parts.Length - 1].Top - 0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, elite, director));
            Assert.That(ex.Message, Does.Contain("Hero.MaxAimHeight"));
            Assert.That(ex.Message, Does.Contain("Director"));
        }

        [Test]
        public void ShippedParts_HeadIsAboutAFifthOfTheColumn()
        {
            // ⭐ THE GUARD OVER THE PHASE'S MAIN NUMBER (finding C-C3): the head
            // is obliged to take 18-26 % of the body's height. v1 gave 36-46 %
            // and turned a shot to the chest into a headshot.
            // ⚠ A GUARD, NOT A WITNESS (lesson 427): the columns are scaled
            // WHOLE, so all five bodies already sit inside the band — 21.48 %
            // (chaser), 22.86 % (collector and gunner), 22.91 % (elite),
            // 22.92 % (director) — and this reads green on the shipped numbers. What it catches is the
            // v1-shaped mistake — one Top raised on its own — whenever it is
            // made, which is the only thing it was ever asked to catch.
            SimConfig cfg = TestConfigs.Default();
            foreach (var parts in new[] { cfg.Chaser.Parts, cfg.Gunner.Parts,
                cfg.Elite.Parts, cfg.Director.Parts, cfg.Hero.Parts })
            {
                HitPart head = parts[parts.Length - 1];
                float column = head.Top;
                float share = (head.Top - head.Bottom) / column;
                Assert.That(share, Is.InRange(0.18f, 0.26f),
                    $"доля головы {share:F3} вне полосы жанра");
            }
        }

        [Test]
        public void ShotAtHeadHeight_ButAtShoulderHalfWidth_Misses()
        {
            // A DIRECT RED AGAINST THE MEASURED DEFECT: today the head carries
            // the shoulders' half-width, so a hit on the shoulder at head
            // height counts as a headshot with the 1.7 multiplier (and a
            // gunner headshot is a oneshot by Д15).
            // ROUND-3 CORRECTION (finding Г-C1): v2 put the mob AT THE VERY
            // POINT it aimed at (`SpawnMobsAt(... new float2(9f, offset))` and
            // `targetXY: new float2(9f, offset)`), so the lateral offset was
            // EXACTLY ZERO and the shot ran straight down the head's own axis.
            // The test claimed a miss -- and would have been red on a CORRECT
            // implementation, leaving mutation M10 without a victim. Its own
            // guard gave no protection either: it checked the NUMBER `offset`,
            // not the geometry of the fixture. Here the spawn and the aim are
            // SEPARATED, and the guard measures the ACTUAL lateral distance
            // from the ray to the body's axis.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
            float headMid = 0.5f * (head.Bottom + head.Top);
            // The aim is offset sideways: wider than the head, narrower than
            // the shoulders.
            float offset = 0.5f * (head.Radius + cfg.Gunner.Radius);
            var shooter = float2.zero;
            var body = new float2(9f, 0f);
            var aim = new float2(9f, offset);
            float2 ray = math.normalize(aim - shooter);
            // Distance from the RAY to the body's axis -- the very number that
            // decides the outcome.
            float lateral = math.abs(ray.x * (body.y - shooter.y) - ray.y * (body.x - shooter.x));
            Assert.Greater(lateral, head.Radius + cfg.Weapon.ProjectileRadius,
                "луч проходит внутри головы — тест ничего не проверяет");
            Assert.Less(lateral, cfg.Gunner.Radius + cfg.Weapon.ProjectileRadius,
                "луч не входит даже в круг ТЕЛА — кандидат не соберётся, и мутация "
                + "«радиус части заменить радиусом тела» останется без точки приложения");
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, body));
            int before = w.MobCount;

            TestWorlds.FireAimed3D(w, shooter, muzzleH: 1f, targetXY: aim, targetH: headMid);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(before, w.MobCount, "выстрел мимо головы на полуширине плеч убил ганнера");
            Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out _),
                "плечо на высоте головы засчитано попаданием");
        }

        [Test]
        public void TopOfTheModel_IsShootable()
        {
            // THE SECOND DIRECT RED: measured in session 43 -- the model is
            // 1.46/1.20/1.37 times taller than its own column, so its crown
            // could not be shot at all.
            //
            // ⚠ THE CHASER IS FROZEN (coordinator Ruling 71), and the number
            // that argues for it is 0.0078. This shot climbs, so its contact
            // height is decided by WHERE along the line it enters the head
            // part's circle -- and a chaser left walking closes on the
            // collector at (159.16, 0), i.e. recedes along the firing line by
            // 0.5 m over the six ticks of flight (Accel 30, MaxSpeed 5.2, minus
            // one tick spent leaving Idle). That moves the entry from x = 5.71
            // to x = 6.21 and the contact height from 2.5703 m to 2.70775 m --
            // 0.0078 m ABOVE head.Top itself, so the test would pass only
            // through the crown-graze clamp rather than through the crown being
            // shootable. A margin of 8 mm is a coincidence, not a margin.
            // Frozen, the contact lands at 2.5703 m, 0.13 m clear of the crown,
            // and the test measures what its name says.
            SimConfig cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f; cfg.Chaser.Accel = 0f;   // Ruling 71, see above
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: head.Top - 0.05f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "макушка модели по-прежнему не простреливается");
            Assert.AreEqual(HitZone.Head, e.Zone, "попадание в макушку — не хедшот");
        }

        [Test]
        public void HitExactlyOnAPartBoundary_BelongsToTheUpperPart()
        {
            // THE BOUNDARY TAKEN IS BODY/HEAD, NOT LEGS/BODY (review finding
            // D-C7). On the legs(R 0.35)/body(R 0.50) seam the "boundary closed
            // on both sides" mutation is indistinguishable: both parts become
            // candidates, but the LARGER radius always yields the SMALLER t
            // (rule C-M4, which this very task cites), so the body wins the
            // min-scan either way and the zone reads Body for the mutant too.
            // On the body(0.50)/head(0.17) seam the same rules give OPPOSITE
            // answers: Head when correct, Body for the mutant.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: head.Bottom,
                targetXY: new float2(6f, 0f), targetH: head.Bottom);   // EXACTLY the boundary
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(HitZone.Head, e.Zone, "граница отдана НИЖНЕЙ части");
        }

        [Test]
        public void ContactHeight_ComesFromTheWinningPart_NotTheBodyCircle()
        {
            // The Т14b witness: the part has its own t, and that is what gives
            // the contact height -- and with it the moment arm. Taken off the
            // body circle the height would be a different number.
            // THE EXPECTATION IS AN EXACT NUMBER, NOT A RANGE (review finding
            // D-C8): the first draft asserted Is.InRange(head.Bottom,
            // head.Top), and the entry heights into the BODY circle (3.533) and
            // into the HEAD circle (3.632) both sit inside [3.24, 4.20] -- so
            // mutation M12 passed. The gap between them is 0.1 m, hence the
            // 0.03 tolerance.
            //
            // ⚠ THE GUNNER IS FROZEN, AND THAT IS WHAT MAKES THE ARITHMETIC
            // ABOVE TRUE (coordinator Ruling 70, class Н-5). Without the
            // freeze this test is RED ON CORRECT CODE. Under Open() the
            // collector stands at Geometry.SpawnPosFor(0, 1, arena) =
            // (159.16, 0) -- straight down +X from the target -- so `dist` is
            // 150.16 m, far outside the gunner's [7.5, 10.5] band, and
            // UpdateGunner (MobAiSystem.cs:349) sends him into Reposition
            // TOWARD the collector on tick one. He therefore recedes ALONG
            // THE FIRING LINE, 0.94444 m over the nine ticks the round is in
            // flight (Accel 25 ramps MoveTowards by 0.833 m/s per tick up to
            // MaxSpeed 4). The head-part entry lands at x = 9.65444 instead of
            // 8.71 and the contact height measured on a fully correct
            // implementation is 3.9178 m against the 3.6324 m this arithmetic
            // predicts -- a 0.2854 m gap against a 0.03 m tolerance. Frozen,
            // the entry is at 8.71 and the two agree to 2e-7.
            // Same freeze, same reason and same two lines as Ruling 17's in
            // ImpactPhysicsTests: a fixture whose ARITHMETIC is the subject may
            // not let the target move out from under it.
            SimConfig cfg = TestConfigs.Open();
            cfg.Gunner.MaxSpeed = 0f; cfg.Gunner.Accel = 0f;   // Ruling 70, see above
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
            float headMid = 0.5f * (head.Bottom + head.Top);
            const float shooterX = 0f, targetX = 9f, muzzleH = 1f;
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(targetX, 0f)));

            TestWorlds.FireAimed3D(w, new float2(shooterX, 0f), muzzleH: muzzleH,
                targetXY: new float2(targetX, 0f), targetH: headMid);
            TestWorlds.RunUntilProjectilesDie(w);

            // Entry into the PART's circle: the round touches the circle of
            // radius head.Radius grown by its own radius, i.e. it stops
            // (head.Radius + ProjectileRadius) short of the body's axis; the
            // height is the linear interpolation from the muzzle to the aim.
            float enterX = targetX - (head.Radius + cfg.Weapon.ProjectileRadius);
            float expectedHeight = muzzleH + (headMid - muzzleH) * (enterX - shooterX) / (targetX - shooterX);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(expectedHeight, e.Height, 0.03f,
                "высота контакта взята у круга ТЕЛА, а не у выигравшей части");
            // THE OTHER HALF OF THE SAME POINT (coordinator Ruling 73, which
            // overrules Ruling 67). "The contact comes from the winning part"
            // is ONE claim with two coordinates, and until this line the XY
            // half had no witness at all: ProjectileSystem.Update used to build
            // the event's position from the min-scan's `bestT`, i.e. the entry
            // into the BODY circle, while the height came from the part. Told
            // apart BY THE NUMBER on the very geometry this test already
            // builds: the part's entry is at x = 8.71, the body's at
            // 9 - (0.50 + 0.12) = 8.38, and the gap between them is exactly
            // (Gunner.Radius - head.Radius) = 0.33 m against a tolerance of
            // 0.05. A `t` left at the RED step's constant 0f would put it at
            // the muzzle, 8.71 m out, and fail the same assertion.
            //
            // `e.Pos.y` IS DELIBERATELY NOT ASSERTED: this shot runs down
            // y = 0, so correct code and every mutant alike answer zero there
            // — no discriminating power, i.e. a tautology (lesson 428).
            Assert.AreEqual(enterX, e.Pos.x, 0.05f,
                "XY-точка контакта взята у круга ТЕЛА, а не у выигравшей части");
        }

        [Test]
        public void HeadHit_CarriesTheHeadMultiplier()
        {
            // Spec test 11: the head multiplier is observed as a NUMBER, not
            // as "zone Head".
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Gunner.Parts[cfg.Gunner.Parts.Length - 1];
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(9f, 0f)));
            var m = w.Mobs[0]; m.Hp = 1e6f; m.Ai = MobAiState.Idle; w.SetMobForTest(0, m);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(9f, 0f), targetH: 0.5f * (head.Bottom + head.Top));
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(cfg.Weapon.Damage * head.DamageMult, e.Amount, 1e-3f,
                "урон по голове не умножен на множитель ЧАСТИ");
        }

        [Test]
        public void ShotUnderTheKnees_IsNotAHeadshot()
        {
            // Spec test 13: the negative half -- a low shot is obliged to be
            // Legs.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart legs = cfg.Chaser.Parts[0];
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m = w.Mobs[0]; m.Hp = 1e6f; m.Ai = MobAiState.Idle; w.SetMobForTest(0, m);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 0.5f * legs.Top,
                targetXY: new float2(6f, 0f), targetH: 0.5f * legs.Top);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(HitZone.Legs, e.Zone, "выстрел под колено засчитан не ногами");
        }

        [Test]
        public void SlidingCollector_IsMissedByAShotOnTheGunnerMuzzleLine()
        {
            // Spec test 14c -- the BEHAVIORAL witness of the slide profile
            // (validation rule 5 in Т13 only checks the data). A flat shot at
            // the gunner's muzzle height passes over the sliding collector and
            // connects with the standing one.
            //
            // OpenField(), NOT Open(), AND THE REASON IS STRUCTURAL (finding
            // Н-4 / coordinator Ruling 14): this fixture moves a collector to
            // (6, 0), which is inside ZoneRadius[0] = 65 m, and a live
            // collector in the CORE is exactly what MatchFlowSystem activates
            // the Director on -- and Activate spawns him at float2.zero
            // UNCONDITIONALLY, i.e. on the very point this test fires from, so
            // the shot would be measuring the Director. OpenField() empties
            // ZoneRadius, and AnyLiveCollectorInCore leaves through its own
            // `arena.ZoneRadius.Length < 2` guard -- unreachable by
            // construction rather than merely unlikely.
            //
            // playerCount: 2 means the world must be ticked through TickAll,
            // never the solo Tick overload (it throws for PlayerCount > 1).
            // TestWorlds.RunUntilProjectilesDie already goes through TickAll
            // (Ruling 13, and its own doc says so), so this fixture is safe.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            var sliding = w.PlayerAt(1); sliding.SlideTimer = 0.5f; w.SetPlayerForTest(1, sliding);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: cfg.Gunner.MuzzleHeight,
                targetXY: new float2(6f, 0f), targetH: cfg.Gunner.MuzzleHeight);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out _),
                "настильный выстрел попал по слайдящему — профиль не понижен");

            var standing = w.PlayerAt(1); standing.SlideTimer = 0f; w.SetPlayerForTest(1, standing);
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: cfg.Gunner.MuzzleHeight,
                targetXY: new float2(6f, 0f), targetH: cfg.Gunner.MuzzleHeight);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out _),
                "тот же выстрел не попал и по СТОЯЧЕМУ — фикстура не о профиле");
        }

        [Test]
        public void TiltedMob_KeepsItsUprightParts()
        {
            // Spec test 50 (Р375): the parts do NOT rotate with the tilt --
            // otherwise a toppled mob would be invulnerable to flat fire and
            // its hit volume would change every tick. The witness: the same
            // geometry connects with an upright body and with a tilted one.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            float headMid = 0.5f * (head.Bottom + head.Top);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m = w.Mobs[0]; m.Hp = 1e6f; m.Ai = MobAiState.Idle;
            m.Tilt = cfg.Chaser.TiltFallAngle * 0.9f;   // almost flat on the ground
            w.SetMobForTest(0, m);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: headMid);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "накренённое тело перестало попадаться — части поехали за креном");
            Assert.AreEqual(HitZone.Head, e.Zone, "зона поехала вслед за креном");
        }
    }
}
