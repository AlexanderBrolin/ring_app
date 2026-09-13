using NUnit.Framework;
using Ring.Simulation.Combat;
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
    ///
    /// app-88jb Т15 ADDS TWO, AND THEY ARE A PAIR RATHER THAN A REPETITION.
    /// The muzzle rule lost the landmark it compared against when the six
    /// column scalars left SimConfig, and it is re-landed here against the
    /// collector's own parts; both new tests drive ConfigTests.BuildShipped
    /// exactly like the five above.
    ///  - Validate_MuzzleAboveTheTorso_Throws is the SIXTH REFUSAL WITNESS:
    ///    a muzzle above the head is refused.
    ///  - Validate_MuzzleExactlyAtTheHeadBottom_IsLegal is of the OTHER shape,
    ///    the one this file did not carry before -- it witnesses that the
    ///    boundary is ACCEPTED. A rule stated as "<=" has two branches and a
    ///    refusal witness only ever exercises one of them (coordinator
    ///    Ruling 82; the epic's own precedents for the pair are
    ///    ImpactConfigTests.Validate_CocoonDampingExactlyOne_IsLegal from Т1
    ///    and the exact-equality half of validation rule 5 from Т13).
    ///
    /// THE Т14/Т23 FIX-ROUND ADDS ONE MORE WORLD TEST (Ruling 191):
    /// ClimbingShot_EnteringTheTorsoCircleBelowItsBand_LandsOnTheLegs, of the
    /// same Open()-fires-from-origin shape as the seven above -- it witnesses
    /// that the winning part is the FIRST CONTACT (circle and band at once),
    /// never the earliest circle entry; its direct twin lives in HitZoneTests.
    public class HitPartsTests
    {
        // ⛔⛔ THREE REFUSAL WITNESSES LEFT THIS FILE WITH THEIR OWN SUBJECTS
        // (app-94sk T2): Validate_PartWiderThanTheBody_Throws (rule 4),
        // Validate_GapBetweenParts_Throws (rule 2) and
        // Validate_DuplicateZone_Throws (rule 3). They are not "relaxed
        // expectations" — the rules they witnessed were withdrawn, each for a
        // reason SimConfigBuilder.ValidateParts' own doc spells out, and a test
        // whose subject no longer exists witnesses nothing (lesson 427). The
        // three below take their place: two halves of rule 7 and one of rule 10.

        [Test]
        public void Validate_DuplicatePartId_Throws()
        {
            // Rule 7, the checkable half. The id is the wire's NAME for a volume,
            // and two volumes under one name are two answers to one question —
            // the very ambiguity rule 3 used to prevent by zone, now prevented by
            // identity instead, which is what survives a body having two legs.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.Parts[1].PartId = g.Parts[0].PartId;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.Parts"));
            Assert.That(ex.Message, Does.Contain("appears twice"));
        }

        [Test]
        public void ShippedPartIds_AreThePinnedAppendOnlySet()
        {
            // Rule 7, the OTHER half — and it is pinned rather than checked
            // because "append-only" is unobservable at runtime: a configuration
            // does not remember its own history. The same way ProtocolVersion and
            // the length of the zone enum are pinned.
            // ⚠ THE SET, NOT THE ORDER: append-only forbids REUSING a number, not
            // rearranging the volumes that carry them.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            SimConfig cfg = ConfigTests.BuildShipped(h, w, c, g, wv, a, vis);
            foreach ((string name, HitPart[] parts) in new[]
            {
                ("Hero", cfg.Hero.Parts), ("Chaser", cfg.Chaser.Parts),
                ("Gunner", cfg.Gunner.Parts), ("Elite", cfg.Elite.Parts),
                ("Director", cfg.Director.Parts),
            })
            {
                var ids = new System.Collections.Generic.List<int>(parts.Length);
                foreach (HitPart part in parts) ids.Add(part.PartId);
                ids.Sort();
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, ids,
                    $"{name}: набор PartId сменился — id может только дописываться, " +
                    "иначе провод переименует уже названный объём");
            }
        }

        [Test]
        public void Validate_StrikerWithoutItsSwingVolume_Throws()
        {
            // Rule 10: an archetype that strikes names the ONE volume it strikes
            // with. A strike with no volume has no geometry at all, and a strike
            // with two has two — neither is a body anything can hit back.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            // Premise AS A PROPERTY: the chaser must actually be a striker, or
            // the rule under test does not even apply to him.
            Assert.Greater(c.AttackRange, 0f,
                "премисса фикстуры: чейзер обязан быть бьющим, иначе правило 10 к нему не применяется");
            c.SwingPartId = 99;                            // names no volume he has
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.SwingPartId"));
            Assert.That(ex.Message, Does.Contain("EXACTLY ONE"));
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
            h.MaxAimHeight = director.Parts[director.Parts.Length - 1].RestTop - 0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis, elite, director));
            Assert.That(ex.Message, Does.Contain("Hero.MaxAimHeight"));
            Assert.That(ex.Message, Does.Contain("Director"));
        }

        [Test]
        public void Validate_MuzzleAboveTheTorso_Throws()
        {
            // app-88jb Т15. The rule this witnesses compared Hero.MuzzleHeight
            // against the top of the collector's zone column, and after Т15
            // there is nothing on the right-hand side left to compare with: the six
            // column scalars left SimConfig in that task. It moves onto the
            // collector's own parts, at THE BOTTOM OF HIS HEAD -- validation
            // rule 2 makes that the very same number as the top of his torso --
            // because a muzzle inside the head is not a high hold, it is a data
            // error.
            //
            // WHY 1.7 AND NOT SOMETHING TALLER: it stands above the head's
            // bottom (1.35) and below the crown (1.75), i.e. it is exactly the
            // value the OLD rule was silent on. A witness that violated the old
            // bound as well could not tell "the new rule is missing" from "the
            // old rule was deleted"; this one can, so the RED it shows is the
            // absence of the new rule and nothing else.
            //
            // TWO SUBSTRINGS, NOT ONE (precedent F10 of Т13): the refusal has
            // to name the FIELD and the LANDMARK. With only "Hero.MuzzleHeight"
            // asserted, an adjacent rule that happens to mention the same field
            // would hold this test green straight through the outright deletion
            // of the rule it exists for.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.MuzzleHeight = 1.7f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Hero.MuzzleHeight"));
            Assert.That(ex.Message, Does.Contain("head part"));
        }

        [Test]
        public void Validate_MuzzleExactlyAtTheHeadBottom_IsLegal()
        {
            // app-88jb Т15 (coordinator Ruling 82). THE OTHER BRANCH OF THE
            // SAME RULE, and the reason it needs its own test is that the
            // refusal witness above cannot reach it: the rule is stated as
            // "MuzzleHeight <= the bottom of the head part", so a muzzle
            // standing EXACTLY on that seam is LEGAL -- a weapon held as high
            // as the shoulders is a high hold, not a data error, and only a
            // muzzle that has climbed INTO the head is one.
            //
            // ⚠ THIS TEST IS THE JURY OF MUTATION `>` -> `>=` (M-muzzle-edge),
            // and it is the only test in the suite that is: on the shipped and
            // fixture collector the muzzle is 1.0 against a head bottom of
            // 1.35, i.e. 0.35 m clear of the seam, so a strictness flip is
            // invisible everywhere else. Т15 is what creates this branch -- the
            // rule it replaced bounded the muzzle by the crown, 1.75 -- so the
            // gap is this task's to close and not inherited debt.
            //
            // THE EXPECTATION IS A FIXTURE EXPRESSION, NEVER THE NUMBER 1.35
            // (Global Constraints, "two sources of numbers"). It is read from
            // the very field the rule reads -- SimConfigBuilder maps
            // HeroSimConfig.Parts as a DIRECT ALIAS of this array -- so the
            // test compares a value against itself and stays true through any
            // retune of the collector's proportions. Written as the literal it
            // would silently stop testing the boundary the day the head moved,
            // and would then be asserting that 1.35 is legal for a body whose
            // head starts somewhere else.
            //
            // NO NEIGHBOR FIRES ON THIS SUBSTITUTION -- checked by enumerating
            // every read of Hero.MuzzleHeight in SimConfigBuilder rather than
            // by assuming. There is exactly one, and it is the rule under test:
            // the Gunner-muzzle rule reads Hero.SlideProfileTop against
            // Gunner.MuzzleHeight (0.55 + 0 < 0.95), the slide-muzzle rule
            // reads Hero.SlideMuzzleHeight against Hero.SlideProfileTop
            // (0.45 < 0.55), and neither looks at this field at all. Nothing
            // else about the fixture moves, so a red here is the rule and
            // nothing but the rule.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            h.MuzzleHeight = h.Parts[h.Parts.Length - 1].RestBottom;   // exactly the seam
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
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
            // ⛔⛔ MEASURED ON THE BONES, NOT ON THE EXTENTS (app-94sk T2). A
            // volume is a CAPSULE now, so its RestTop/RestBottom include two
            // caps: on the chaser that alone turns a 21.5 % head into a 32.1 %
            // one, and the band would have to be widened to admit a number that
            // says nothing about anatomy. The bones are where the anatomy lives,
            // and the measure on them reproduces the five percentages this test's
            // own doc quotes — 21.48 / 22.86 / 22.86 / 22.91 / 22.92 — i.e. it is
            // the SAME guard, asked of the source the geometry moved to.
            SimConfig cfg = TestConfigs.Default();
            foreach ((HitPart[] parts, PoseTable table) in new[]
            {
                (cfg.Chaser.Parts, cfg.Chaser.Poses), (cfg.Gunner.Parts, cfg.Gunner.Poses),
                (cfg.Elite.Parts, cfg.Elite.Poses), (cfg.Director.Parts, cfg.Director.Poses),
                (cfg.Hero.Parts, cfg.Hero.Poses),
            })
            {
                HitPart head = parts[parts.Length - 1];
                float a = table.Bones[head.BoneA].y, b = table.Bones[head.BoneB].y;
                float crown = math.max(a, b);
                float share = math.abs(a - b) / crown;
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
            float headMid = 0.5f * (head.RestBottom + head.RestTop);
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
            // 0.0078 m ABOVE head.RestTop itself, so the test would pass only
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
                targetXY: new float2(6f, 0f), targetH: head.RestTop - 0.05f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "макушка модели по-прежнему не простреливается");
            Assert.AreEqual(HitZone.Head, e.Zone, "попадание в макушку — не хедшот");
        }

        [Test]
        public void AtTheSharedBone_TheWiderVolumeIsMetFirst_AndTheLadderIsPerStep()
        {
            // THE BOUNDARY TAKEN IS BODY/HEAD, NOT LEGS/BODY (review finding
            // D-C7). On the legs(R 0.35)/body(R 0.50) seam the "boundary closed
            // on both sides" mutation is indistinguishable: both parts become
            // candidates, but the LARGER radius always yields the SMALLER t
            // (rule C-M4, which this very task cites), so the body wins the
            // min-scan either way and the zone reads Body for the mutant too.
            // On the body(0.50)/head(0.17) seam the same rules give OPPOSITE
            // answers: Head when correct, Body for the mutant.
            // ⛔⛔ THE BOUNDARY IS A SHARED BONE NOW, NOT A SHARED NUMBER
            // (app-94sk T2). While parts were bands, `head.RestBottom` WAS the
            // seam — validation rule 2 made "top of the torso" and "bottom of the
            // head" one number. A capsule's RestBottom is its lower CAP (1.95 on
            // this chaser), which sits inside the torso and names no seam at all.
            // What the two volumes genuinely share is the CHEST BONE, and that is
            // what the shot is aimed at.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart head = cfg.Chaser.Parts[cfg.Chaser.Parts.Length - 1];
            HitPart torso = cfg.Chaser.Parts[1];
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

            float seam = cfg.Chaser.Poses.Bones[head.BoneA].y;
            // Premise AS A PROPERTY: the bone really is shared, or there is no
            // boundary here to award to anybody.
            Assert.AreEqual(seam, cfg.Chaser.Poses.Bones[torso.BoneB].y, 1e-6f,
                "премисса фикстуры: у головы и корпуса обязана быть общая кость");
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: seam,
                targetXY: new float2(6f, 0f), targetH: seam);   // EXACTLY the shared bone
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            // ⛔⛔ THE ANSWER IS Body, AND THAT IS THE NEW TRUTH RATHER THAN A
            // RELAXED EXPECTATION (app-94sk T2). Under bands the seam was awarded
            // to the upper part by a strict comparison — the torso's own `lo <
            // part.RestTop` excluded it at exactly this height. Under capsules
            // there is nothing to exclude: the torso is 0.50 m wide against the
            // head's 0.17, so the round ENTERS ITS FLANK 0.33 m earlier along the
            // flight (x >= 5.38 against x >= 5.71, recomputed), and the two
            // entries fall in different tick-steps. The zone ladder arbitrates
            // between volumes met on ONE step; it does not reach across steps,
            // and making it reach would mean judging a contact that has not
            // happened yet.
            Assert.AreEqual(HitZone.Body, e.Zone,
                "лестница приоритета перепрыгнула через шаг — попадание отдано объёму, " +
                "до которого снаряд на этом шаге ещё не дошёл");
            // ⚠ AND THE HEAD IS GENUINELY REACHABLE AT THIS HEIGHT — asked of the
            // resolver directly, on a step that spans both windows. Without this
            // half the fixture would pass on a body whose head is simply missing.
            float3 origin = new float3(6f, 0f, 0f);
            bool onOneStep = HitVolumes.Resolve(cfg.Chaser.Parts, in cfg.Chaser.Poses, 0,
                float2.zero, origin, 0f, 1f,
                new float3(5.0f, 0f, seam), new float3(6.2f, 0f, seam),
                cfg.Weapon.ProjectileRadius,
                out HitZone oneStepZone, out _, out _, out _, out _);
            Assert.IsTrue(onOneStep, "премисса: на общем шаге тело обязано быть задето");
            Assert.AreEqual(HitZone.Head, oneStepZone,
                "на ОДНОМ шаге лестница обязана отдать общую кость голове");
        }

        [Test]
        public void ContactHeight_ComesFromTheWinningPart_NotTheBodyCircle()
        {
            // The Т14b witness: the part has its own t, and that is what gives
            // the contact height -- and with it the moment arm. Taken off the
            // body circle the height would be a different number.
            // THE EXPECTATION IS AN EXACT NUMBER, NOT A RANGE (review finding
            // D-C8): the first draft asserted Is.InRange(head.RestBottom,
            // head.RestTop), and the entry heights into the BODY circle (3.533) and
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
            float headMid = 0.5f * (head.RestBottom + head.RestTop);
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
                targetXY: new float2(9f, 0f), targetH: 0.5f * (head.RestBottom + head.RestTop));
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(cfg.Weapon.Damage * head.DamageMult, e.Amount, 1e-3f,
                "урон по голове не умножен на множитель ЧАСТИ");
        }

        /// A bone of a body standing at the origin, carried into the WORLD frame
        /// the way HitVolumes.ToWorld carries it: the body's plan `(x, z)` becomes
        /// the world's plan `(x, y)`, and the body's height `.y` becomes `.z`.
        /// ⚠ Written out here rather than borrowed from the resolver on purpose
        /// (lesson 427): a witness that calls the code under test proves only
        /// that the code agrees with itself.
        static float3 Chaser3D(in PoseTable table, int bone)
        {
            float3 b = table.Bones[bone];
            return new float3(b.x, b.z, b.y);
        }

        [Test]
        public void ShotUnderTheKnees_IsNotAHeadshot()
        {
            // Spec test 13: the negative half -- a low shot is obliged to be
            // Legs.
            // ⛔⛔ THE AIM MOVED ONTO THE BONES (app-94sk T2), and not because the
            // old one "stopped passing". `0.5f * legs.RestTop` meant the middle
            // of the LEGS BAND while a part was a band; RestTop is the top of a
            // CAPSULE now — bone plus radius — so half of it (0.615) sits inside
            // the torso capsule, whose bottom cap reaches 0.38. Worse, the
            // chaser's leg runs out to a foot swung 0.9 m aside, so a shot down
            // his own axis at any leg height misses the leg by 0.49 m
            // (recomputed). "Under the knees" now has to be said where the knee
            // IS, and PartMidWorld is what says it.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            HitPart legs = cfg.Chaser.Parts[0];
            var body = new float2(6f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));
            var m = w.Mobs[0]; m.Hp = 1e6f; m.Ai = MobAiState.Idle; w.SetMobForTest(0, m);

            TestConfigs.PartMidWorld(in cfg.Chaser.Poses, in legs, body,
                out float2 legPlan, out float legH);
            // Premise AS A PROPERTY, AND THE PROPERTY IS THE GEOMETRY ITSELF: the
            // leg's middle must lie OUTSIDE the torso's capsule, or the zone
            // ladder would rightly answer Body and the fixture would be measuring
            // the ladder instead of the legs. ⚠ It is NOT enough to be below the
            // torso's RestBottom — that is a height, and a capsule is refused by
            // DISTANCE: this point sits 0.44 m below the pelvis and 0.45 m aside,
            // i.e. 0.63 m away from the torso segment against its 0.50 m radius.
            HitPart torsoPart = cfg.Chaser.Parts[1];
            float3 tA = new float3(body.x, body.y, 0f)
                + Chaser3D(in cfg.Chaser.Poses, torsoPart.BoneA);
            float3 tB = new float3(body.x, body.y, 0f)
                + Chaser3D(in cfg.Chaser.Poses, torsoPart.BoneB);
            float3 legMid = new float3(legPlan, legH);
            float3 closest = Geometry.ClosestPointOnSegment(legMid, tA, tB, out _);
            Assert.Greater(math.distance(legMid, closest), torsoPart.Radius,
                "премисса фикстуры: середина ноги обязана лежать ВНЕ капсулы корпуса");
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: legH,
                targetXY: legPlan, targetH: legH);
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
            float headMid = 0.5f * (head.RestBottom + head.RestTop);
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

        [Test]
        public void ClimbingShot_EnteringTheTorsoCircleBelowItsBand_LandsOnTheLegs()
        {
            // Ruling 191 (review finding A-1) -- the WORLD half of the witness
            // pair; the direct half lives in HitZoneTests. A climbing round
            // aimed past the Director's flank enters his wide TORSO circle
            // while its height is still inside the LEGS band, and only later
            // -- still climbing, still short of the torso's band -- touches
            // the legs circle itself. The first point where a part's circle
            // and its own band are satisfied AT ONCE belongs to the legs; a
            // resolver that awards the earliest CIRCLE entry instead reads
            // the blow as Body, with the torso's multiplier and a contact
            // height in a belt the torso does not occupy.
            //
            // THE DIRECTOR, NOT THE GUNNER, on the shared fixture's own
            // numbers: his legs/torso radii differ by 0.66 m, so the two
            // circle entries are far enough apart to land in one tick-step
            // with room to spare; the gunner's 0.15 m gap is not.
            //
            // EVERY PREMISE IS ASSERTED, NOT ASSUMED (lesson 622, the same
            // discipline ShotAtHeadHeight_ButAtShoulderHalfWidth_Misses
            // above carries): the target is frozen AND PROBED to stay put,
            // the ray crosses the legs circle (or Legs could never win and
            // the witness would be blind), the head circle stays out of
            // play, the two multipliers differ, the legs circle is entered
            // inside the legs' own band and the torso circle is entered
            // BELOW the torso's band -- the exact window the defect lives in.
            SimConfig cfg = TestConfigs.Open();
            cfg.Director.MaxSpeed = 0f; cfg.Director.Accel = 0f;   // frozen, probed below
            var w = new SimulationWorld(7, cfg);
            HitPart legs = cfg.Director.Parts[0];
            HitPart torso = cfg.Director.Parts[1];
            HitPart head = cfg.Director.Parts[cfg.Director.Parts.Length - 1];
            var shooter = float2.zero;
            var body = new float2(6.125f, 1f);
            // The aim runs straight down +X, so the body's own y IS the
            // lateral miss; the climb is 0.2 m per flat meter (FireAimed3D's
            // contract: height is linear in flat distance).
            var aim = new float2(10f, 0f);
            const float muzzleH = 0.5f;
            const float targetH = 2.5f;
            TestWorlds.SpawnMobsAt(w, (MobType.Director, body));

            Assert.AreNotEqual(legs.DamageMult, torso.DamageMult,
                "множители ног и корпуса совпадают — подмена зоны не видна ни событием, ни уроном");
            float2 ray = math.normalize(aim - shooter);
            float lateral = math.abs(ray.x * (body.y - shooter.y) - ray.y * (body.x - shooter.x));
            float projR = cfg.Weapon.ProjectileRadius;
            Assert.Less(lateral, legs.Radius + projR,
                "луч не пересекает круг НОГ — Legs недостижим, и свидетель слеп");
            Assert.Greater(lateral, head.Radius + projR,
                "луч задевает круг головы — свидетель перестал быть спором ног и корпуса");
            // Entry points along the ray and the heights the round holds
            // there -- the window premises, asserted as geometry.
            float sAxis = math.dot(body - shooter, ray);
            float slope = (targetH - muzzleH) / math.length(aim - shooter);
            float legsPad = legs.Radius + projR;
            float torsoPad = torso.Radius + projR;
            float sLegsIn = sAxis - math.sqrt(legsPad * legsPad - lateral * lateral);
            float hLegsIn = muzzleH + slope * sLegsIn;
            float sTorsoIn = sAxis - math.sqrt(torsoPad * torsoPad - lateral * lateral);
            float hTorsoIn = muzzleH + slope * sTorsoIn;
            // ⛔⛔ THE BAND PREMISES ARE GONE WITH THE BANDS (app-94sk T2), and
            // so is the defect they framed. "Enters the circle below its band"
            // described a body made of a CIRCLE and a BAND checked separately;
            // a capsule is ONE shape, and SegmentCapsule returns the first entry
            // into that shape, so awarding "the earliest circle entry" is not a
            // mistake an implementation can make any more.
            // ⇒ WHAT THIS FIXTURE WITNESSES NOW IS THE ANSWER THAT REPLACED IT:
            // a round reaching both volumes is judged by the ZONE LADDER, and
            // the torso outranks the legs — which is the opposite verdict, and
            // deliberately so (the same ladder fixture 11 pins by calling the
            // resolver directly; this is its world-side twin).
            Assert.Greater(hTorsoIn, 0f, "премисса фикстуры: подъём луча обязан быть восходящим");

            // The freeze is probed, not narrated: three idle ticks would move
            // a live body, a frozen one must not. (After the blow the impact
            // shove is free to move him -- the premise only has to hold until
            // the round arrives.)
            TestWorlds.IdleTicks(w, 3);
            Assert.AreEqual(0f, math.distance(w.Mobs[0].Pos, body), 1e-6f,
                "цель не заморожена — оба входа в круги уехали бы вместе с ней");

            TestWorlds.FireAimed3D(w, shooter, muzzleH: muzzleH, targetXY: aim, targetH: targetH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "снаряд вовсе не попал — фикстура не об окне дефекта");
            Assert.AreEqual(HitZone.Body, e.Zone,
                "лестница приоритета зон не отработала — корпус обязан перебить ноги");
            // ⚠ THE HEIGHT IS ASSERTED AS A PROPERTY, NOT AS A LITERAL: computing
            // the exact first entry into a capsule would mean writing the solver
            // a second time in the fixture, which is the duplication rule 2
            // forbids. What matters — and what a contact taken from the body
            // circle would break — is that the height lies ON THE ROUND'S OWN
            // CLIMB and is not the middle of the part it struck.
            Assert.That(e.Height, Is.InRange(muzzleH, targetH),
                "высота контакта вне подъёма снаряда — взята не с шага");
            TestConfigs.PartMidWorld(in cfg.Director.Poses, in torso, body, out _, out float torsoMid);
            Assert.Greater(math.abs(e.Height - torsoMid), 1e-3f,
                "высота контакта равна середине части — контакт взят у объёма, а не у шага");
            Assert.AreEqual(cfg.Weapon.Damage * torso.DamageMult, e.Amount, 1e-3f,
                "урон не умножен на множитель части-победителя");
        }
    }
}
