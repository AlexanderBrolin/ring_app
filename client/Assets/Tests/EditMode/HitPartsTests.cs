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
            // ⛔⛔ app-saqr (T4b): THE SETS ARE PER-ARCHETYPE NOW, and they have
            // to be — the ids are LOCAL (spec §3.2, the Jedi Academy precedent
            // in COMBAT-001 §3.7: one flat global table ran out of bits and the
            // boss's parts were cut out of it). The gunner's 3..8 are his LEGS
            // while the collector's 3..6 are ARMS, so one shared expectation
            // would be a claim about a numbering that does not exist.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            SimConfig cfg = ConfigTests.BuildShipped(h, w, c, g, wv, a, vis);
            foreach ((string name, HitPart[] parts) in new[]
            {
                ("Hero", cfg.Hero.Parts), ("Chaser", cfg.Chaser.Parts),
                ("Gunner", cfg.Gunner.Parts), ("Elite", cfg.Elite.Parts),
                ("Director", cfg.Director.Parts),
            })
            {
                // ⛔ THE EXPECTATION IS BUILT FROM THE LENGTH, NOT SPELLED OUT:
                // what rule 7 forbids is a GAP or a REUSE, i.e. the ids of N
                // volumes must be exactly 0..N-1. Literals would pin the COUNT
                // a second time — fixture 44 already does that, and does it on
                // the shipped layout rather than on whatever a caller put in
                // the slot (`MakeDefaults` fills the gunner's with a fresh
                // `MobConfig`, which is chaser-shaped by class).
                var expected = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++) expected[i] = i;

                var ids = new System.Collections.Generic.List<int>(parts.Length);
                foreach (HitPart part in parts) ids.Add(part.PartId);
                ids.Sort();
                CollectionAssert.AreEqual(expected, ids,
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

        // ⛔ THE WITNESS OF VALIDATION RULE 5 IS GONE WITH ITS RULE (app-94sk
        // T4, owner decision Н60). It pinned "Hero.SlideProfileTop coincides
        // with a part boundary", which was a meaningful claim only while parts
        // were a contiguous COLUMN of bands — validation rule 2, withdrawn in
        // T2. Capsules on bone pairs overlap and are not sorted, so there is no
        // boundary set left to land on. ⚠ THE SUBJECT DID NOT VANISH, IT MOVED:
        // `Validate_SlideCrownAboveTheGunnersMuzzle_Throws` below is rule 16,
        // and it asks the two questions this family actually carried — that the
        // gunner's round passes over a sliding collector, and that the
        // collector does not fire from above his own silhouette — of the CROWN
        // OF THE SLIDE BY THE TABLE rather than of a scalar.

        [Test]
        public void Validate_MaxAimHeightBelowTheDirectorsCrown_Throws()
        {
            // Rule 14 grows to FOUR archetypes: today the Director takes no part
            // in it, and his head would be unreachable by any aim at all
            // (finding C-I1).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var (elite, director) = ConfigTests.MakeShippedArchetypes();
            // ⛔ app-saqr (T4b): THE CROWN, NOT THE LAST VOLUME. Rule 14
            // measures the ceiling against the TOP OF EVERY volume, and with
            // fifteen of them laid out on real bones the tallest is a gun, not
            // whatever happens to be last in the array. `HitParts.RestCrown` is
            // the one home of that question, and the rule itself reads it.
            h.MaxAimHeight = HitParts.RestCrown(director.Parts) - 0.1f;
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
            // ⛔ app-saqr (T4b): the head BY ZONE — see the note on fixture 28а.
            HitPart heroHead = TestWorlds.VolumeOfZone(h.Parts, HitZone.Head, "сборщик");
            h.MuzzleHeight = heroHead.RestBottom;   // exactly the seam
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void ShippedParts_HeadNeverReachesTheLowerThird()
        {
            // ⭐ THE GUARD OVER THE PHASE'S MAIN NUMBER (finding C-C3): v1 of
            // this geometry raised ONE Top on its own, the head took 36-46 % of
            // the body, and a shot to the chest became a headshot.
            //
            // ⛔⛔ app-saqr (T4b): THE MEASURE CHANGED BECAUSE THE GEOMETRY DID,
            // and the old one is not merely inconvenient — it no longer exists.
            // It read "the head's share of the column" off the DISTANCE BETWEEN
            // ITS TWO BONES, which a column had and a capsule does not: the
            // chaser's and the gunner's heads are DEGENERATE capsules (one bone,
            // |a−b| = 0, share 0), while the elite's and the Director's Head
            // zone IS their whole shell by spec §3.2/D-I8. A band of 18-26 %
            // could only be met by a body shaped like the thing that was
            // replaced.
            // ⇒ WHAT SURVIVES IS THE SUBJECT: a head that grows DOWNWARD is a
            // chest shot scoring as a headshot. So the head's own extent must
            // stay out of the body's lower third — the same defect, asked of
            // the capsule's real reach instead of a column's fractions.
            // ⚠ THREE BODIES, NOT FIVE: the elite and the Director carry
            // `HitZone.Head` on their SHELL (spec §3.2, finding D-I8 — the 1.7
            // multiplier and `SpawnFullExplodeGibs` hang on the zone, and
            // neither has an anatomical head), so "the head must sit high" is
            // not a claim about them at all.
            // ⚠ A GUARD, NOT A WITNESS (lesson 427): all three clear it on the
            // shipped numbers — 1.19 / 1.71 / 1.88 m against thirds at
            // 0.49 / 0.77 / 1.32 — and what it catches is the change that
            // would not.
            SimConfig cfg = TestConfigs.Default();
            foreach ((string name, HitPart[] parts) in new[]
            {
                ("collector", cfg.Hero.Parts), ("chaser", cfg.Chaser.Parts),
                ("gunner", cfg.Gunner.Parts),
            })
            {
                HitPart head = TestWorlds.VolumeOfZone(parts, HitZone.Head, name);
                float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
                for (int i = 0; i < parts.Length; i++)
                {
                    top = math.max(top, parts[i].RestTop);
                    bottom = math.min(bottom, parts[i].RestBottom);
                }
                float third = bottom + (top - bottom) / 3f;
                Assert.Greater(head.RestBottom, third,
                    $"{name}: объём головы опустился в нижнюю треть тела — выстрел в корпус " +
                    "засчитается хедшотом, ровно та ошибка, которой этот сторож поставлен");
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
            HitPart head = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Head, "чейзер");
            float headMid = 0.5f * (head.RestBottom + head.RestTop);
            // The aim is offset sideways: wider than the head, narrower than
            // the shoulders.
            // ⛔⛔ app-saqr (T4b): THE BODY IS THE CHASER, AND A MEASUREMENT
            // forced it. The fixture needs a lane OUTSIDE the head and INSIDE
            // the body circle — that IS "the shoulder at head height" — and the
            // gunner has none: his head is a cabin 1.19 m wide against a
            // physical circle of 0.50, so the two bounds cross and no offset
            // satisfies both. The chaser keeps the shape the fixture is about
            // (a 0.25 m head inside a 0.50 m circle); the claim, the mutation
            // it feeds and the ladder are the same on either body.
            // ⚠ THE MIDDLE OF THE LANE, not of the two radii: each bound is
            // grown by the round's own radius, and the lane is 0.25 m wide.
            float offset = 0.5f * ((head.Radius + cfg.Weapon.ProjectileRadius)
                + (cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius));
            var shooter = float2.zero;
            var body = new float2(9f, 0f);
            var aim = new float2(9f, offset);
            float2 ray = math.normalize(aim - shooter);
            // Distance from the RAY to the body's axis -- the very number that
            // decides the outcome.
            float lateral = math.abs(ray.x * (body.y - shooter.y) - ray.y * (body.x - shooter.x));
            Assert.Greater(lateral, head.Radius + cfg.Weapon.ProjectileRadius,
                "луч проходит внутри головы — тест ничего не проверяет");
            Assert.Less(lateral, cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius,
                "луч не входит даже в круг ТЕЛА — кандидат не соберётся, и мутация "
                + "«радиус части заменить радиусом тела» останется без точки приложения");
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));
            int before = w.MobCount;

            TestWorlds.FireAimed3D(w, shooter, muzzleH: 1f, targetXY: aim, targetH: headMid);
            TestWorlds.RunUntilProjectilesDie(w);

            // ⛔⛔ app-saqr (T4b): THE CLAIM IS "NOT A HEADSHOT", NOT "NOT A HIT",
            // and the difference is the layout itself. On the three bands this
            // fixture was written against, a lane outside the head at head
            // height met NOTHING — the body was a stack of coaxial circles. A
            // chaser laid out on his own bones has an ARM out there (his
            // forearm reaches 0.55 m off the axis at 1.51-1.91 m), so the round
            // lands, and it SHOULD: it passed through a shoulder. What must not
            // happen is the shoulder paying the head's 1.7, which is the defect
            // this fixture was opened for and the mutation it feeds ("size a
            // part by the body's radius" would swell the head over this lane).
            Assert.AreEqual(before, w.MobCount, "выстрел мимо головы на полуширине плеч убил тело");
            if (TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent shoulder))
                Assert.AreNotEqual(HitZone.Head, shoulder.Zone,
                    "плечо на высоте головы засчитано ХЕДШОТОМ — ровно тот дефект, "
                    + "ради которого голова перестала нести полуширину плеч");
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
            HitPart head = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Head, "чейзер");
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: head.RestTop - 0.05f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "макушка модели по-прежнему не простреливается");
            Assert.AreEqual(HitZone.Head, e.Zone, "попадание в макушку — не хедшот");
        }

        [Test]
        public void HitExactlyOnTheSharedBone_BelongsToTheUpperPart()
        {
            // THE BOUNDARY TAKEN IS BODY/HEAD, NOT LEGS/BODY (review finding
            // D-C7): on the legs/body seam the "boundary closed on both sides"
            // mutation is indistinguishable, because the larger radius always
            // yields the smaller t and the torso wins the scan either way. On
            // the body/head seam the same rules give OPPOSITE answers.
            //
            // ⛔⛔ app-saqr (T4b): THE SEAM IS AN OVERLAP NOW, NOT A SHARED BONE.
            // T2 moved it from a shared NUMBER (rule 2 made "top of torso" and
            // "bottom of head" one height) to a shared BONE; on real bones there
            // is no third form to move to — the chaser's head is a sphere on his
            // head bone and his chest spans Chest→Neck, and the two OVERLAP
            // rather than touch. That overlap IS the boundary a round can be
            // aimed at, and awarding it is exactly what the zone ladder is for.
            // ⚠ THE PREMISE IS ASSERTED AS GEOMETRY: the aim point must lie
            // inside BOTH capsules, or there is no boundary here to award.
            // ⛔ AND THE SHOT IS LEVEL: the head sits INSIDE the chest capsule,
            // so both volumes must be candidates on ONE tick-step — a climbing
            // shot enters the chest 0.58 m earlier and the step can end between
            // them, which reads Body on entirely correct code.
            SimConfig cfg = TestConfigs.Open();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(7, cfg);
            HitPart head = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Head, "чейзер");
            var body = new float2(6f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));

            float3 headBone = cfg.Chaser.Poses.Bones[head.BoneA];
            float seam = headBone.y;
            Assert.Less(DistanceToSegmentInBodyFrame(in cfg.Chaser.Poses, in head, headBone),
                head.Radius, "премисса: точка прицела внутри капсулы ГОЛОВЫ");
            // ⛔ WHICH torso volume overlaps is asked of the GEOMETRY, not of
            // `TryFindByZone`: that one answers "a volume of this zone" by the
            // resolver's own ranking, and a body has SEVERAL torso volumes now
            // (this chaser has six — torso, chest and four arm segments). The
            // boundary is with whichever of them reaches the head, and on him
            // that is the CHEST rather than the first in the array.
            bool overlapped = false;
            foreach (HitPart candidate in cfg.Chaser.Parts)
            {
                if (candidate.Zone != HitZone.Body) continue;
                if (DistanceToSegmentInBodyFrame(in cfg.Chaser.Poses, in candidate, headBone)
                    >= candidate.Radius) continue;
                overlapped = true; break;
            }
            Assert.IsTrue(overlapped,
                "премисса: та же точка внутри капсулы КОРПУСА — иначе границы нет");

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: seam,
                targetXY: body, targetH: seam);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e));
            Assert.AreEqual(HitZone.Head, e.Zone, "граница отдана НИЖНЕЙ части");
        }

        /// How far a point in the BODY frame sits from a volume's own segment,
        /// in that same frame. ⚠ Body frame throughout: no `ToWorld` here, and
        /// that is the point — a premise about the LAYOUT must not depend on
        /// where the body was spawned.
        static float DistanceToSegmentInBodyFrame(in PoseTable table, in HitPart part, float3 p)
        {
            float3 a = table.Bones[part.BoneA], b = table.Bones[part.BoneB];
            return math.distance(p, Geometry.ClosestPointOnSegment(p, a, b, out _));
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
            // app-94sk T6b: and his TURN is stopped too -- a gunner in
            // Reposition/Fire squares up to his target by the course law
            // (T5c) whatever a fixture sets, and since T6b the volumes turn
            // with him: his head bone sits 0.17 m off his axis in plan, which
            // the 0.05 m tolerance below cannot absorb. The rate is a config
            // number (T5b); zero holds the table's orientation
            // (TestWorlds.FaceTheTable's own doc).
            cfg.Gunner.MobTurnDegPerSec = 0f;
            var w = new SimulationWorld(7, cfg);
            HitPart head = TestWorlds.VolumeOfZone(cfg.Gunner.Parts, HitZone.Head, "ганнер");
            float headMid = 0.5f * (head.RestBottom + head.RestTop);
            const float shooterX = 0f, targetX = 9f, muzzleH = 1f;
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(targetX, 0f)));
            // app-94sk T6b: the arithmetic below puts the head ON the axis,
            // where the table keeps it; a gunner turned to face the shooter
            // carries it 0.17 m aside (his head bone's own plan offset).
            TestWorlds.FaceTheTable(w, 0);

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
            HitPart head = TestWorlds.VolumeOfZone(cfg.Gunner.Parts, HitZone.Head, "ганнер");
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
            // the torso capsule, whose bottom cap reaches 0.38 — and the ladder
            // then rightly answers Body. ⚠ THE LEG ITSELF IS STILL REACHABLE DOWN
            // THE AXIS (recomputed: such a shot passes 0.189 m from the leg's
            // segment against its 0.35 m radius, i.e. it HITS) — the zone is lost
            // to the torso, not to a miss. "Under the knees" therefore has to be
            // said where ONLY the knee is, and PartMidWorld is what says it.
            SimConfig cfg = TestConfigs.Open();
            // app-94sk T6b: frozen BY CONFIG, before the constructor -- the
            // volumes turn with the course now, and a chaser left to chase
            // turns 18 degrees a tick towards his velocity while the round is
            // in flight, carrying the shin out of the plan PartMidWorld named
            // (`m.Ai = Idle` below does not freeze him: FreezeArchetype's doc).
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(7, cfg);
            // ⛔⛔ app-saqr (T4b): WHICH LEG IS A QUESTION ABOUT THE BODY NOW.
            // `Parts[0]` was a leg while parts were a column sorted bottom-to-
            // top; on real bones it is the torso. And "any leg" is not enough
            // either: the fixture chaser used to carry a foot swung 0.9 m out of
            // his circle BY HAND, so every leg point was clear of the torso,
            // while his measured rest pose keeps his legs under him — his thigh
            // sits 0.40 m off the axis against a torso capsule of 0.29 plus the
            // round's 0.12. The point that IS nobody else's sits BELOW the
            // torso, on a shin, and `TestConfigs.TryFindCleanVolume` is the one
            // home of that search.
            Assert.IsTrue(TestConfigs.TryFindCleanVolume(cfg.Chaser.Parts, in cfg.Chaser.Poses,
                    HitZone.Legs, cfg.Weapon.ProjectileRadius, out HitPart legs),
                "премисса фикстуры: у чейзера есть объём ноги, середина которого не накрыта " +
                "ни одним объёмом другой зоны — иначе лестница зон отдаст удар корпусу");
            var body = new float2(6f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));
            var m = w.Mobs[0]; m.Hp = 1e6f; m.Ai = MobAiState.Idle; w.SetMobForTest(0, m);
            // app-94sk T6b: PartMidWorld speaks in the table's frame, and so
            // must the body (TestWorlds.FaceTheTable's own doc).
            TestWorlds.FaceTheTable(w, 0);

            TestConfigs.PartMidWorld(in cfg.Chaser.Poses, in legs, body,
                out float2 legPlan, out float legH);
            // Premise AS A PROPERTY, AND THE PROPERTY IS THE GEOMETRY ITSELF: the
            // leg's middle must lie OUTSIDE the torso's capsule, or the zone
            // ladder would rightly answer Body and the fixture would be measuring
            // the ladder instead of the legs. ⚠ It is NOT enough to be below the
            // torso's RestBottom — that is a height, and a capsule is refused by
            // DISTANCE: this point sits 0.44 m below the pelvis and 0.45 m aside,
            // i.e. 0.63 m away from the torso segment against its 0.50 m radius.
            HitPart torsoPart = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Body, "чейзер");
            float3 tA = new float3(body.x, body.y, 0f)
                + Chaser3D(in cfg.Chaser.Poses, torsoPart.BoneA);
            float3 tB = new float3(body.x, body.y, 0f)
                + Chaser3D(in cfg.Chaser.Poses, torsoPart.BoneB);
            float3 legMid = new float3(legPlan, legH);
            float3 closest = Geometry.ClosestPointOnSegment(legMid, tA, tB, out _);
            // ⛔ THE THRESHOLD IS THE SUM OF THE RADII, NOT THE TORSO'S ALONE:
            // the round has its own 0.12, and a premise stated against 0.50 would
            // pass while the shot still met the torso. ⚠ MEASURED MARGIN: 0.629
            // against 0.62, i.e. 9 mm — thin, and named so rather than left to be
            // discovered when T4b changes a radius.
            Assert.Greater(math.distance(legMid, closest), torsoPart.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: середина ноги обязана лежать ВНЕ капсулы корпуса с учётом радиуса снаряда");
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
        public void TiltedMob_LeansItsPartsWithIt()   // app-94sk T6b: Р375 withdrawn (spec §3.6, risk Р-K)
        {
            // ⛔⛔ THE OPPOSITE OF WHAT THIS FIXTURE PINNED UNTIL T6b. It was
            // `TiltedMob_KeepsItsUprightParts`: "the parts do not follow the
            // tilt" (Р375), asked as a comparison of two worlds, one upright
            // and one leaning, whose zones had to agree. Spec §3.6 withdraws
            // Р375 -- the tilt enters the pose key and the volumes go down with
            // the body (the price is named in risk Р-K and paid by fixture
            // 26a) -- so the same comparison now has to DISAGREE: at 90 % of
            // the fall angle towards +x the chaser's head drops from 1.97 to
            // 1.36 m and leaves the line a head-height shot runs along, while
            // the neck end of his chest capsule, laid over away from the
            // shooter, rises into it. Upright the ladder awards the head;
            // leaning, the body.
            // The world is the witness, so the caller's hand-off of the tilt
            // is covered too, not only ToWorld.
            SimConfig cfg = TestConfigs.Open();
            HitPart head = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Head, "чейзер");
            float headMid = 0.5f * (head.RestBottom + head.RestTop);

            // ⛔ THE PARAMETER IS A `float2`, AND THAT IS NOT A TYPE TIDY-UP
            // (app-94sk T5a). `m.Tilt = tilt` with a `float` would still
            // COMPILE against the vectorized field — `Unity.Mathematics` has
            // an implicit `float -> float2` — and would silently lean the body
            // (t, t): at the call below that is length 1.1455 rad against a
            // fall angle of 0.9, so "almost flat on the ground" would become
            // "past the threshold" and the fixture would change its subject
            // with nothing to report it.
            // ⛔ FROZEN BY CONFIG, BEFORE THE CONSTRUCTOR (FreezeArchetype's own
            // doc): the round flies six meters, and an unfrozen chaser walks
            // 0.173 m a tick towards the shooter meanwhile. The tilt spring is
            // NOT frozen by it and keeps settling the lean back towards
            // upright; the shot is spawned close so the lean it meets is the
            // one the fixture set (fixture 26's own note).
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);

            HitZone Shoot(float2 tilt)
            {
                var w = new SimulationWorld(7, cfg);
                TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
                var m = w.Mobs[0]; m.Hp = 1e6f;
                m.Dir = new float2(0f, 1f);   // the table's own orientation: the yaw is the identity
                m.Tilt = tilt;
                m.TiltVel = float2.zero;
                w.SetMobForTest(0, m);
                w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(5.2f, 0f),
                    new float2(cfg.Weapon.ProjectileSpeed, 0f), headMid, velZ: 0f,
                    cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime);
                TestWorlds.RunUntilProjectilesDie(w);
                return TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent hit)
                    ? hit.Zone : HitZone.None;
            }

            HitZone upright = Shoot(float2.zero);
            Assert.AreEqual(HitZone.Head, upright,
                "премисса фикстуры: стоящему телу выстрел на высоте головы попадает в голову");
            // 90% of the fall angle ON ONE AXIS, away from the shooter -- the
            // length is the angle, so this is the same "almost flat" the
            // scalar used to state.
            HitZone leaning = Shoot(new float2(cfg.Chaser.TiltFallAngle * 0.9f, 0f));
            // The BODY, not merely "not the head": the comment above commits
            // to the neck end of the chest capsule rising into the line
            // (recomputed: 0.72 from the line against its 0.95 reach, while
            // the head is 0.59 away against 0.37), and a resolver that lost
            // the body altogether would satisfy an AreNotEqual.
            Assert.AreEqual(HitZone.Body, leaning,
                "зона не поехала вслед за креном — объёмы остались стоять, пока тело лежит (Р375 снят)");
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
            // ⛔⛔ app-saqr (T4b): THE BODY IS THE CHASER NOW, AND A MEASUREMENT
            // forced the move. The fixture needs a lane where the LEGS and the
            // TORSO are both reachable and the HEAD is not — that is the whole
            // of "a dispute between legs and torso". The Director cannot offer
            // one any more: his `HitZone.Head` is his SHELL (spec §3.2, finding
            // D-I8 — the zone carries his 1.7 multiplier and his exploding
            // death), a 2.76 m sphere that covers every lane his legs are in.
            // The chaser keeps the shape the fixture is about: at 1.0 m his
            // torso (0.52-1.65) and his thighs (0.44-1.34) both answer while
            // his head (1.71-2.22) is out of reach entirely.
            SimConfig cfg = TestConfigs.Open();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(7, cfg);
            HitPart torso = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Body, "чейзер");
            HitPart head = TestWorlds.VolumeOfZone(cfg.Chaser.Parts, HitZone.Head, "чейзер");
            var body = new float2(6f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));

            // The aim point: the middle of the TORSO's own segment, which the
            // legs reach too. Asked of the geometry, not of a literal height.
            TestConfigs.PartMidWorld(in cfg.Chaser.Poses, in torso, body,
                out float2 torsoPlan, out float torsoH);
            Assert.Less(torsoH, head.RestBottom,
                "премисса: высота прицела ниже головы — иначе свидетель перестал быть спором "
                + "ног и корпуса");

            // ⛔ THE LEG HAS TO OVERLAP THE AIM, AND THAT IS THE OPPOSITE OF A
            // "clean" leg: this fixture is ABOUT the dispute, so it needs the
            // leg volume that shares this height with the torso — the thigh,
            // not the shin `TryFindCleanVolume` would hand back.
            HitPart legs = default;
            bool legFound = false;
            foreach (HitPart candidate in cfg.Chaser.Parts)
            {
                if (candidate.Zone != HitZone.Legs) continue;
                if (torsoH <= candidate.RestBottom || torsoH >= candidate.RestTop) continue;
                legs = candidate; legFound = true; break;
            }
            Assert.IsTrue(legFound,
                "премисса: на высоте прицела есть объём ноги — иначе Legs недостижим и спора нет");
            Assert.AreNotEqual(legs.DamageMult, torso.DamageMult,
                "множители ног и корпуса совпадают — подмена зоны не видна ни событием, ни уроном");

            TestWorlds.IdleTicks(w, 3);
            Assert.AreEqual(0f, math.distance(w.Mobs[0].Pos, body), 1e-6f,
                "цель не заморожена — оба входа в круги уехали бы вместе с ней");

            // ⛔ THE SHOT CLIMBS, and the tail of this fixture is why: it pins
            // that the contact height lies ON THE ROUND'S OWN CLIMB and is NOT
            // the middle of the part it struck. A level shot would satisfy the
            // first by construction and fail the second, proving neither.
            const float muzzleH = 0.5f;
            float targetH = torsoH;
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: muzzleH,
                targetXY: torsoPlan, targetH: targetH);
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
            TestConfigs.PartMidWorld(in cfg.Chaser.Poses, in torso, body, out _, out float torsoMid);
            Assert.Greater(math.abs(e.Height - torsoMid), 1e-3f,
                "высота контакта равна середине части — контакт взят у объёма, а не у шага");
            Assert.AreEqual(cfg.Weapon.Damage * torso.DamageMult, e.Amount, 1e-3f,
                "урон не умножен на множитель части-победителя");
        }

        // ---- app-94sk T4: the five validation rules the baker makes possible ----

        /// The crown of the SLIDE by the table, repeated here ON PURPOSE
        /// (lesson 427): a witness that calls the code it checks proves only
        /// that the code agrees with itself. The slide is clip
        /// `BakedClips.Collector.Slide`; the crown is the highest bone end of
        /// any volume, grown by that volume's own radius.
        /// ⛔ THE CLIP INDEX IS A NAMED CONSTANT, NOT THE LITERAL 1: on a
        /// one-clip table `ClipFirstRow[1]` is the row-count sentinel, and the
        /// read would run past the bones instead of going red.
        static float SlideCrownOf(Ring.Data.HeroConfig hero)
        {
            ref PoseTable t = ref ConfigTests.PoseTableFor(hero);
            int row = t.ClipFirstRow[BakedClips.Collector.Slide];
            if (row >= t.Bones.Length / t.BoneCount)
                throw new System.ArgumentException("pose table has no slide clip");
            float top = float.NegativeInfinity;
            foreach (HitPart part in hero.Parts)
            {
                float a = t.Bones[row * t.BoneCount + part.BoneA].y + part.Radius;
                float b = t.Bones[row * t.BoneCount + part.BoneB].y + part.Radius;
                top = math.max(top, math.max(a, b));
            }
            return top;
        }

        [Test]
        public void Validate_BoneIndexPastTheTable_Throws()   // rule 6, test 28в, M384
        {
            // ⛔ THE VIOLATION GOES ON `BoneB` DELIBERATELY: `BoneA` is read
            // first by every other rule, so a mutant that checked only the
            // first half of the pair would survive a fixture that broke it.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.Parts[1].BoneB = (byte)(ConfigTests.PoseTableFor(c).BoneCount + 1);
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.Parts[1].BoneB"));
            Assert.That(ex.Message, Does.Contain("outside the pose table"));
        }

        [Test]
        public void Validate_GatherRadiusBelowTheTableMaximum_Throws()   // rule 9, test 4б, M379
        {
            // ⛔ TWO HALVES, AND THE VIOLATION GOES ON THE SECOND. `>= Radius`
            // holds on all five bodies without any rule at all, so a mutant
            // that kept only that half would never be caught by breaking it.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.GatherRadius = c.Radius;            // >= Radius satisfied, the table maximum is not
            Assert.Greater(HitParts.GatherReach(c.Parts, in ConfigTests.PoseTableFor(c)), c.Radius,
                "премисса: объёмы чейзера выходят за его физический круг — иначе нарушать нечего");
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.GatherRadius"));
            Assert.That(ex.Message, Does.Contain("pose table"));
        }

        [Test]
        public void Validate_ABodyWithNoPoseTable_Throws()   // rule 12, the empty half
        {
            // ⛔⛔ THE MOST DANGEROUS REFUSAL THERE IS, AND THE ONE NOTHING ELSE
            // REPORTS. A body whose table never arrived resolves every volume
            // against a table with no rows, refuses every shot, and is SILENTLY
            // UNHITTABLE — no gate goes red for it: the bootstrap is idempotent,
            // the EditMode set reads `TestConfigs`, and the golden hashes are
            // pinned to fixtures.
            // ⛔ AN EMPTY TABLE IN A LIVE CARRIER, NOT A NULL CARRIER, AND THE
            // DIFFERENCE IS THE WHOLE FIXTURE: `BuildShipped` fills a null one
            // in with a fixture table on purpose (so that ~45 tests varying one
            // field do not go red on this rule), so a null here would be
            // quietly repaired and the witness would prove nothing. An empty
            // table is the state the rule is actually about — the carrier
            // arrived, the numbers did not.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.Poses = ConfigTests.FixtureTable(default);
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.Poses"));
            Assert.That(ex.Message, Does.Contain("empty"));
        }

        [Test]
        public void Validate_NaNInThePoseTable_Throws()   // rule 12, test 28б, M383
        {
            // ⛔⛔ THE MOST DANGEROUS REFUSAL THERE IS: a NaN in a bone makes a
            // body UNHITTABLE SILENTLY — `Geometry.SegmentCapsule` answers
            // false on NaN by contract, and the checksum does not catch it
            // (NaN is an ordinary bit pattern to fold).
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            ConfigTests.PoseTableFor(c).Bones[3] = new float3(float.NaN, 0f, 0f);
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Poses"));
            Assert.That(ex.Message, Does.Contain("finite"));
        }

        [Test]
        public void Validate_RestTopDisagreeingWithTheTable_Throws()   // rule 13, test 28а, M357
        {
            // `RestBottom`/`RestTop` are DERIVED data. Without this rule they
            // would be a third source of truth about a body's geometry, beside
            // the table and the volumes themselves.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.Parts[2].RestTop += 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.Parts[2].RestTop"));
            Assert.That(ex.Message, Does.Contain("rest pose"));
        }

        [Test]
        public void Validate_RestBottomDisagreeingWithTheTable_Throws()   // rule 13, lower half
        {
            // ⛔ THE OTHER HALF OF RULE 13, AND IT NEEDS ITS OWN FIXTURE. The
            // rule checks `RestBottom` and `RestTop` separately, so a mutant
            // that dropped the `RestBottom` comparison would survive the
            // `RestTop` witness next door untouched — the same argument that
            // put rule 6's violation on `BoneB` rather than `BoneA`.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.Parts[2].RestBottom -= 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Chaser.Parts[2].RestBottom"));
            Assert.That(ex.Message, Does.Contain("rest pose"));
        }

        [Test]
        public void Validate_GatherRadiusBelowTheBodyCircle_Throws()   // rule 9, lower half
        {
            // ⛔ THE FIRST HALF OF RULE 9 — the one the table half cannot cover.
            // Rule 9 REPLACES the withdrawn rule 4 (`Parts[i].Radius <= Radius`,
            // spec §3.13), so the replacement owes a witness of its own; without
            // it a mutant keeping only the table comparison is green everywhere.
            //
            // ⛔⛔ AND IT CANNOT BE ISOLATED BY CONSTRUCTION, WHICH IS SAID HERE
            // RATHER THAN WORKED AROUND: isolating it would need a body whose
            // volumes stay INSIDE its physical circle, and no fixture body has
            // one — the collector's widest capsule IS his radius (0.45 = 0.45)
            // and every other table is wider still. So BOTH halves refuse this
            // configuration, and what this fixture pins is the FIRST half's own
            // MESSAGE, which the second half does not produce. A mutant that
            // drops `gatherRadius < radius` still throws — on the table half —
            // and still fails here, because the words it names are different.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.GatherRadius = g.Radius * 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.GatherRadius must be >= Gunner.Radius"));
        }

        [Test]
        public void Validate_SlideCrownAboveTheGunnersMuzzle_Throws()   // rule 16, test 4в
        {
            // ⛔⛔ TWO HALVES, AND BOTH ARE BROKEN HERE — separately, because a
            // mutant that dropped one would survive a fixture that only broke
            // the other.
            //   (1) the gunner's round must pass OVER a sliding collector;
            //   (2) the collector must not fire from above his own silhouette.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            g.MuzzleHeight = SlideCrownOf(h) * 0.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Gunner.MuzzleHeight"));
            Assert.That(ex.Message, Does.Contain("slide crown"));

            var (h2, w2, c2, g2, wv2, a2, vis2) = ConfigTests.MakeDefaults();
            h2.SlideMuzzleHeight = SlideCrownOf(h2) + 0.1f;
            var ex2 = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h2, w2, c2, g2, wv2, a2, vis2));
            Assert.That(ex2.Message, Does.Contain("Hero.SlideMuzzleHeight"));
        }

        // ================= app-saqr (plan T4b): THE LAYOUT ITSELF ============

        [Test]
        public void EveryBodyCarriesTheLayoutItsSpecPromises()   // test 44, M401
        {
            // ⛔ THE COUNT IS THE ONE THING PINNED BY A LITERAL, and it is a
            // CHARACTERIZATION PIN of exactly the kind
            // `SimConfig_CarriesExactlyTwelveFields` is: it does not prove the
            // layout right, it makes whoever changes it come here and decide.
            // The numbers are spec §3.2's, and they are a consequence of the
            // measured rigs rather than a wish — COMBAT-001 §2.2 derives each
            // one from the bones that body actually has.
            SimConfig cfg = TestConfigs.Default();
            Assert.AreEqual(11, cfg.Hero.Parts.Length,
                "collector: pelvis, chest, head, upper and lower arm x2, thigh and shin x2");
            Assert.AreEqual(13, cfg.Chaser.Parts.Length,
                "chaser: torso, chest, head, upper and lower arm x2, leg of 3 segments x2");
            Assert.AreEqual(9, cfg.Gunner.Parts.Length,
                "gunner: torso, chest, head, leg of 3 segments x2 — he has no arms at all");
            Assert.AreEqual(13, cfg.Elite.Parts.Length,
                "elite: body, 2 guns, leg of 4 segments plus a foot x2");
            Assert.AreEqual(15, cfg.Director.Parts.Length,
                "director: body, 2 guns, leg of 3 segments x4");
        }

        [Test]
        public void TheLegsLeaveAGapAShotCanPassThrough()   // test 45, M402
        {
            // ⭐⭐ THIS IS MILESTONE ITEM 1, CHECKED BY MACHINE RATHER THAN BY
            // EYE: "the round passes BETWEEN THE LEGS". A body whose legs are
            // one capsule on its own axis cannot honour it, and nothing else in
            // the suite would notice — every other fixture shoots AT a volume.
            //
            // ⛔ THE GAP IS MEASURED BETWEEN LEGS, NOT BETWEEN SEGMENTS: the
            // segments of ONE leg share bones and touch by construction, so a
            // naive pairwise minimum would be negative on correct data. Which
            // segments form one leg is read off the bones they share
            // (`LegsApart` below), never off names — the table carries none.
            //
            // ⛔⛔ AND IT IS MEASURED AT THE DISTAL SEGMENTS, WHICH IS WHAT MAKES
            // "THE NARROWEST" ASKABLE AT ALL. Legs MEET at the body — the
            // Director's front and rear hips sit 0.91 m apart and his thighs are
            // 0.74 m thick, so up there the gap is −0.57 m on a perfectly correct
            // layout, and that is anatomy rather than a defect. Taking the WIDEST
            // gap instead would dodge that, and it dodges too much: with four
            // legs a collapsed INNER pair stays invisible behind a healthy outer
            // one, which is exactly what milestone item 8 exists to see. So the
            // measure is the NARROWEST gap between the FURTHEST-DOWN segment of
            // each leg — where legs are apart if they are apart anywhere, and
            // where a pair sharing an axis reads −2r at once.
            // ⚠ MEASURED, and the threshold is the plan's own `2 ProjectileRadius`
            // rather than a bare zero: chaser +0.3636, gunner +0.6722, elite
            // +0.6536, Director +0.3178 against 0.24.
            //
            // ⛔⛔ THE COLLECTOR IS OUT, AND THE NUMBER SAYS WHY RATHER THAN THE
            // SILENCE THAT USED TO: his own shins OVERLAP in the plan — the gap
            // is −0.1229 m, because his feet stand 0.40 m apart in his rest pose
            // while the two shin capsules are 0.2604 m thick each. That is a
            // fact about the measured collector, not about this fixture, and it
            // is booked (bd, discovered from app-w4ca) so the milestone sees it.
            SimConfig cfg = TestConfigs.Default();
            foreach (var body in new[]
                     {
                         ("Chaser", cfg.Chaser.Parts, cfg.Chaser.Poses),
                         ("Gunner", cfg.Gunner.Parts, cfg.Gunner.Poses),
                         ("Elite", cfg.Elite.Parts, cfg.Elite.Poses),
                         ("Director", cfg.Director.Parts, cfg.Director.Poses),
                     })
            {
                HitPart[] legs = System.Array.FindAll(body.Item2, x => x.Zone == HitZone.Legs);
                // ⚠ LEGS, NOT LEG VOLUMES: the chaser carries six volumes on two
                // legs, so counting the array would answer a different question
                // from the one the sentence asks.
                float gap = LegsApart(legs, body.Item3, row: 0, out int legCount);
                Assert.GreaterOrEqual(legCount, 2, $"{body.Item1}: premise — more than one leg");
                Assert.Greater(gap, 2f * cfg.Weapon.ProjectileRadius,
                    $"{body.Item1}: its legs close up — «the round passes between the legs» "
                    + "is unreachable on this layout");
            }
        }

        [Test]
        public void TheGunnerHasNoArmVolumes()   // test 46, GUARD (lesson 427), M403
        {
            // ⛔ A GUARD, NOT A WITNESS, and it is named as one: it is green on
            // the three-band layout this task replaces, because a three-band
            // body has no arms either. Its witness is mutation M403 ("arms
            // handed to the gunner too").
            //
            // ⛔⛔ THE PROPERTY IS GEOMETRIC, NOT A `PartId` RANGE. A range
            // cannot express this at all: ids are LOCAL to an archetype, and
            // the gunner's 3..8 are his LEGS — so any range covering the
            // collector's arms would call the gunner's legs arms and this
            // fixture would be red on correct code (review finding A-C2/D-C5).
            // What actually distinguishes a body without arms is where its
            // torso volumes sit: an arm IS a torso-zone volume swung out as far
            // as a leg. So: every Body-zone volume of the gunner stays closer
            // to his own axis than ANY of his legs.
            //
            // ⚠ THE PREMISE MAKES IT NON-TAUTOLOGICAL: the chaser, who HAS
            // arms, violates the very same property — checked here, or a
            // property true of every body would witness nothing (lesson 428).
            SimConfig cfg = TestConfigs.Default();
            Assert.Less(WidestTorsoReach(cfg.Gunner.Parts, cfg.Gunner.Poses),
                NearestLegReach(cfg.Gunner.Parts, cfg.Gunner.Poses),
                "the gunner was given arms — a torso volume of his reaches out as far as a leg, "
                + "and his rig has no arm bones at all (spec §3.2)");
            Assert.Greater(WidestTorsoReach(cfg.Chaser.Parts, cfg.Chaser.Poses),
                NearestLegReach(cfg.Chaser.Parts, cfg.Chaser.Poses),
                "premise: the chaser HAS arms, so the property above must fail on him — "
                + "otherwise it holds of every body and witnesses nothing");
        }

        /// The NARROWEST gap in the body's PLAN between the LOWEST segments of
        /// two DIFFERENT legs, the capsule radii taken off — see fixture 45 for
        /// why the lowest segments and not the whole leg. ⛔ WHICH VOLUMES ARE
        /// ONE LEG IS DERIVED,
        /// NOT DECLARED: two segments belong to the same leg when they share a
        /// bone, and sharing is transitive — a knee joins thigh to shin, the
        /// shin joins the foot. That makes the grouping work unchanged for the
        /// Director's FOUR legs, which is what milestone item 8 measures.
        /// ⚠ THE PLAN IS `(x, z)`: `.y` is the height in the body frame
        /// (`HitVolumes.ToWorld`), so a gap measured with it would be the
        /// distance between a knee and an ankle.
        static float LegsApart(HitPart[] legs, in PoseTable t, int row, out int legCount)
        {
            int stride = t.BoneCount;
            int rowBase = row * stride;

            // Union-find over the segments, joined by a shared bone.
            var group = new int[legs.Length];
            for (int i = 0; i < legs.Length; i++) group[i] = i;
            for (int i = 0; i < legs.Length; i++)
                for (int j = 0; j < i; j++)
                {
                    if (!SharesABone(in legs[i], in legs[j])) continue;
                    int gi = Root(group, i), gj = Root(group, j);
                    if (gi != gj) group[gi] = gj;
                }

            // ⛔ ONE SEGMENT PER LEG — THE LOWEST — AND THAT IS WHAT MAKES THE
            // NARROWEST GAP AN HONEST QUESTION (see the fixture's own doc). A
            // leg's upper segments live at the body, where legs genuinely meet;
            // its lowest one is where a path between them either exists or does
            // not. "Lowest" is read off the bones, never off names.
            var distal = new System.Collections.Generic.Dictionary<int, int>(legs.Length);
            for (int i = 0; i < legs.Length; i++)
            {
                int g = Root(group, i);
                if (!distal.TryGetValue(g, out int best)
                    || LowestBoneY(in legs[i], in t, rowBase) < LowestBoneY(in legs[best], in t, rowBase))
                    distal[g] = i;
            }
            legCount = distal.Count;

            float gap = float.PositiveInfinity;
            var chosen = new System.Collections.Generic.List<int>(distal.Values);
            for (int x = 0; x < chosen.Count; x++)
                for (int y = 0; y < x; y++)
                {
                    int i = chosen[x], j = chosen[y];
                    float between = SegmentsApart(
                        Plan(t.Bones[rowBase + legs[i].BoneA]), Plan(t.Bones[rowBase + legs[i].BoneB]),
                        Plan(t.Bones[rowBase + legs[j].BoneA]), Plan(t.Bones[rowBase + legs[j].BoneB]))
                        - legs[i].Radius - legs[j].Radius;
                    if (between < gap) gap = between;
                }
            // A body with fewer than two legs has no gap to report, and the
            // caller's own premise is what says so.
            return chosen.Count < 2 ? float.NegativeInfinity : gap;

            static int Root(int[] g, int i)
            {
                while (g[i] != i) i = g[i];
                return i;
            }
        }

        static float LowestBoneY(in HitPart part, in PoseTable t, int rowBase) =>
            math.min(t.Bones[rowBase + part.BoneA].y, t.Bones[rowBase + part.BoneB].y);

        static bool SharesABone(in HitPart a, in HitPart b) =>
            a.BoneA == b.BoneA || a.BoneA == b.BoneB || a.BoneB == b.BoneA || a.BoneB == b.BoneB;

        static float2 Plan(float3 bone) => new float2(bone.x, bone.z);

        /// The distance between two segments in the plan. ⛔ FOUR POINT-TO-
        /// SEGMENT ANSWERS, which is the minimum for a pair of segments that do
        /// not cross — and legs that DO cross are what this measurement exists
        /// to refuse, so the crossing case reading zero rather than negative
        /// costs nothing: the caller subtracts two radii from it and a crossed
        /// pair is refused either way.
        /// ⚠ `Geometry.ClosestPointOnSegment` is the project's own primitive
        /// for this half, reused rather than re-derived.
        static float SegmentsApart(float2 a0, float2 a1, float2 b0, float2 b1)
        {
            float d = math.min(
                math.min(PointToSegment(a0, b0, b1), PointToSegment(a1, b0, b1)),
                math.min(PointToSegment(b0, a0, a1), PointToSegment(b1, a0, a1)));
            return d;

            static float PointToSegment(float2 p, float2 a, float2 b) =>
                math.distance(p, Geometry.ClosestPointOnSegment(p, a, b, out _));
        }

        /// How far from the body's own axis the furthest TORSO-zone volume
        /// reaches, and how far the NEAREST leg does — the pair fixture 46
        /// compares. Rest row, because that is the pose a rig is authored in.
        static float WidestTorsoReach(HitPart[] parts, in PoseTable t) =>
            ExtremeReach(parts, in t, HitZone.Body, widest: true);

        static float NearestLegReach(HitPart[] parts, in PoseTable t) =>
            ExtremeReach(parts, in t, HitZone.Legs, widest: false);

        static float ExtremeReach(HitPart[] parts, in PoseTable t, HitZone zone, bool widest)
        {
            float best = widest ? 0f : float.PositiveInfinity;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Zone != zone) continue;
                float reach = math.max(math.length(Plan(t.Bones[parts[i].BoneA])),
                                       math.length(Plan(t.Bones[parts[i].BoneB])));
                best = widest ? math.max(best, reach) : math.min(best, reach);
            }
            return best;
        }
    }
}
