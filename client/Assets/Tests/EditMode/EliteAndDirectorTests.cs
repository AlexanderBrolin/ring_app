using System.Collections.Generic;
using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 10 (spec Р213/Р251, ledger R-28, coordinator's fourteen-
    /// branch table): the two new mob archetypes, Elite and Director, and
    /// the FIVE two-way branches this task owns (Simulation + Protocol —
    /// Presentation's own six are Т31's job, Ф7). Elite reuses the existing
    /// six-value MobAiState FSM wholesale (Р214) — no new state, so
    /// MaxMobAiStateValue never moves.
    ///
    /// Fixture numbers throughout are DELIBERATELY NOT spec §3.13's real
    /// MobEliteConfig/MobDirectorConfig asset numbers (Elite MaxHp 120/
    /// Radius 0.8, Director MaxHp 2500/Radius 2.2) — Global Constraints
    /// (R-56): expectations in tests are fixture EXPRESSIONS, a literal
    /// copied from a future `.asset` is a review finding. They ARE
    /// deliberately distinct from Chaser's/Gunner's own TestConfigs numbers
    /// too, so a mutation that quietly reused either existing section
    /// cannot hide behind an accidental numeric coincidence.
    public class EliteAndDirectorTests
    {
        [Test]
        public void MobConfigFor_ReturnsOwnConfig_ForEachOfFourArchetypes()
        {
            // Mutation: collapse SimulationWorld.MobConfigFor's four-way
            // switch back to today's two-way ternary
            // (`type == Chaser ? _config.Chaser : _config.Gunner`) — Elite
            // and Director both silently read Gunner's own section, and
            // this test's third/fourth assertions catch it (R-41 note: all
            // four assertions share this ONE mutation family — a genuinely
            // independent per-case regression, e.g. a four-way switch
            // swapping Elite's and Director's own arms, is caught the same
            // way, since each assertion compares against ITS OWN section's
            // value and Elite's own assertion runs and fails before
            // Director's is ever reached).
            var c = TestConfigs.Open();
            c.Elite = new MobSimConfig { MaxHp = 58f, Radius = 0.65f };
            c.Director = new MobSimConfig { MaxHp = 91f, Radius = 0.95f };
            var w = new SimulationWorld(1, c);
            Assert.AreEqual(c.Chaser, w.MobConfigFor(MobType.Chaser));
            Assert.AreEqual(c.Gunner, w.MobConfigFor(MobType.Gunner));
            Assert.AreEqual(c.Elite, w.MobConfigFor(MobType.Elite));
            Assert.AreEqual(c.Director, w.MobConfigFor(MobType.Director));
        }

        [Test]
        public void MobConfigFor_UnknownArchetype_Throws()
        {
            // Mutation: drop the `default` arm's throw and fall back to a
            // silent return (e.g. `_ => _config.Gunner`) — a weakening, not
            // a flip (R-42): it disables the guard branch rather than
            // reversing a comparison that was never on a boundary.
            var w = new SimulationWorld(1, TestConfigs.Open());
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => w.MobConfigFor((MobType)99));
        }

        [Test]
        public void SpawnedElite_StartsWithEliteMaxHp()
        {
            // Subject is Elite, NOT Chaser (ledger 227) — Chaser already
            // worked under today's ternary, so asserting on it would prove
            // nothing about this task's own change.
            // Mutation (brief Step 4, named explicitly): revert SpawnMob's
            // Hp assignment from the switch back to
            // `type == Chaser ? Chaser.MaxHp : Gunner.MaxHp` — Elite falls
            // to Gunner.MaxHp (20f in TestConfigs.Open(), != 58f here).
            var c = TestConfigs.Open();
            c.Elite = new MobSimConfig { MaxHp = 58f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Elite, new float2(5f, 0f));
            Assert.AreEqual(58f, w.Mobs[0].Hp);
        }

        [Test]
        public void SpawnedDirector_StartsWithDirectorMaxHp()
        {
            // Same mutation family as SpawnedElite_StartsWithEliteMaxHp
            // above (the ternary revert) — Director falls to Gunner.MaxHp
            // (20f in TestConfigs.Open(), != 91f here) too.
            var c = TestConfigs.Open();
            c.Director = new MobSimConfig { MaxHp = 91f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Director, new float2(5f, 0f));
            Assert.AreEqual(91f, w.Mobs[0].Hp);
        }

        /// "Enhanced chaser with ranged finishing" (spec Р214): borrows its
        /// melee numbers from Chaser and its ranged numbers from Gunner —
        /// fixture EXPRESSIONS off TestConfigs.Open(), not literals (Global
        /// Constraints), so a balance retune to either real archetype
        /// cannot silently desync this fixture from the two profiles it
        /// stands in for. Radius/MaxHp/ContactDamage are Elite's own
        /// (distinct from both parents, same "no accidental coincidence"
        /// reasoning as the other tests in this file); LegsTop/BodyTop/
        /// HeadTop/*DamageMult are set so a hit resolves cleanly (Elite has
        /// no real hit-zone profile yet — Т12 delivers one).
        static SimConfig EliteHybridConfig()
        {
            var c = TestConfigs.OpenField();
            c.Elite = new MobSimConfig
            {
                MaxSpeed = c.Chaser.MaxSpeed, Accel = c.Chaser.Accel,
                AttackRange = c.Chaser.AttackRange, TelegraphSeconds = c.Chaser.TelegraphSeconds,
                AttackCooldown = c.Chaser.AttackCooldown,
                SwingLeadFactor = c.Chaser.SwingLeadFactor, SwingLeadMaxMeters = c.Chaser.SwingLeadMaxMeters,
                SeparationRadius = c.Chaser.SeparationRadius, SeparationStrength = c.Chaser.SeparationStrength,
                AvoidLookahead = c.Chaser.AvoidLookahead, AvoidMargin = c.Chaser.AvoidMargin,
                PreferredRange = c.Gunner.PreferredRange, RangeTolerance = c.Gunner.RangeTolerance,
                StrafeSpeed = c.Gunner.StrafeSpeed, FireInterval = c.Gunner.FireInterval,
                ProjectileSpeed = c.Gunner.ProjectileSpeed, ProjectileRadius = c.Gunner.ProjectileRadius,
                ProjectileLifetime = c.Gunner.ProjectileLifetime, ProjectileDamage = c.Gunner.ProjectileDamage,
                LeadFactor = c.Gunner.LeadFactor, MuzzleHeight = c.Gunner.MuzzleHeight,
                Radius = 0.65f, MaxHp = 58f, ContactDamage = c.Chaser.ContactDamage,
                LegsTop = c.Gunner.LegsTop, BodyTop = c.Gunner.BodyTop, HeadTop = c.Gunner.HeadTop,
                LegsDamageMult = 1f, BodyDamageMult = 1f, HeadDamageMult = 1f,
            };
            return c;
        }

        [Test]
        public void EliteUsesAllSixAiStates_OverDistanceSweep()
        {
            // Spec Р214: no new MobAiState is added — Elite is documented
            // to visit all six EXISTING values over the course of a fight.
            // Two fixed-distance sub-runs sweep the domain rather than one
            // continuously-moving mob (the exact distance thresholds the
            // eventual dispatch picks are a GREEN/Step-3 decision, not
            // pinned here): a melee-range spawn drives the chaser half
            // (Idle -> Chase -> Telegraph -> Recover, same setup
            // Chaser_TelegraphThenStrike_DamagesPlayer in MobAiTests.cs
            // already relies on) and a far spawn drives the gunner half
            // (Idle -> Reposition -> Fire, same setup
            // Gunner_KeepsPreferredRange_AndFiresOnlyWithLoS already relies
            // on). Their UNION is the six-state domain.
            //
            // Mutation: whatever Step 3 dispatch MobAiSystem.Update grows
            // for Elite, reverting it back to today's two-way
            // `if (Type == Chaser) UpdateChaser(...) else UpdateGunner(...)`
            // (MobAiSystem.cs:61) is the kill — Elite is not Chaser, so
            // EVERY tick, at EITHER spawn distance, runs UpdateGunner alone
            // and Chase/Telegraph/Recover are never observed.
            var seen = new HashSet<MobAiState>();

            {
                var c = EliteHybridConfig();
                var w = new SimulationWorld(1, c);
                w.SpawnMobForTest(MobType.Elite, new float2(1.0f, 0f)); // well inside AttackRange
                seen.Add(w.Mobs[0].Ai); // Idle, straight out of SpawnMob
                for (int i = 0; i < 40; i++)
                {
                    w.Tick(default);
                    seen.Add(w.Mobs[0].Ai);
                }
            }
            {
                var c = EliteHybridConfig();
                var w = new SimulationWorld(1, c);
                w.SpawnMobForTest(MobType.Elite, new float2(20f, 0f)); // well outside PreferredRange
                for (int i = 0; i < 300; i++)
                {
                    w.Tick(default);
                    seen.Add(w.Mobs[0].Ai);
                }
            }

            CollectionAssert.AreEquivalent(
                new[]
                {
                    MobAiState.Idle, MobAiState.Chase, MobAiState.Telegraph, MobAiState.Recover,
                    MobAiState.Reposition, MobAiState.Fire,
                },
                seen);
        }

        [Test]
        public void ProjectileGather_UsesEliteRadius_NotGunnerRadius()
        {
            // RENAMED (Pack B, coordinator finding #3 — was
            // ProjectileGather_UsesArchetypeRadius_ForElite): the old name
            // promised more than any fixture in this method can prove.
            // "Archetype radius" has TWO homes, and this test can only ever
            // see one of them:
            //   * GATHER (Combat/ProjectileSystem.cs, `MobRadiusFor` — this
            //     method's own name now says so) — a Stage 2 decision, its
            //     own doc explains why: a per-mob MobConfigFor(...) call in
            //     this loop would copy the whole MobSimConfig struct once
            //     per candidate in the hottest loop in the simulation.
            //     THIS is the switch the fixture below actually exercises,
            //     and its witness is mutation A2 (Pack A, already confirmed
            //     — reverting `Elite => eliteRadius` to `gunnerRadius`
            //     starves gather of any candidate at all).
            //   * ACCEPT (ProjectileSystem.AcceptCandidate, :386) reads
            //     `w.MobConfigFor(mob.Type).Radius` instead — a SEPARATE,
            //     independent re-derivation. Mutation B1 (MobConfigFor's
            //     `Elite => _config.Elite` swapped to `_config.Chaser`)
            //     left THIS test green twice in a row (Pack B, both before
            //     and after a descending-shot rewrite) because
            //     AcceptCandidate's own fallback (:488-491 doc, "a
            //     hypothetical disagreement can only ever let a hit
            //     through, never silently swallow one") makes its radius
            //     read structurally unobservable through gather-plus-accept
            //     alone — gather already found the candidate honestly (its
            //     OWN radius is untouched by B1), so accept's wrong radius
            //     never gets a chance to reject anything. No fixture in
            //     THIS test — level or descending — can make B1 visible
            //     here; MobRadiusFor_AgreesWith_MobConfigFor_
            //     ForEveryArchetype below is B1's actual witness, comparing
            //     the two homes directly instead of inferring their
            //     agreement through projectile physics.
            //
            // The descending shot itself stays (coordinator: "he's more
            // honest than the old fixture, and earned his keep") — it is a
            // strictly better test of the GATHER switch than the old
            // constant-height one, even though it turned out not to be
            // what closes B1. Every number below is hand-derived from
            // Geometry.SegmentCircleInterval's and HitZones' own formulas,
            // then cross-checked against a Python re-implementation of the
            // same formulas (not guessed, not run in Unity — still
            // unavailable to me):
            //   Mob at (0,0), Elite.Radius 2.6. Shot spawns at (-3, 1.5),
            //   vel (180, 0) — ONE tick (dt = 1/30 s) covers exactly 6 m,
            //   landing at (3, 1.5): p0=(-3,1.5), p1=(3,1.5) for tick 1.
            //   height 1.5, velZ -30 (drops 1.0 m over that same tick).
            //   f = p0 - mob = (-3, 1.5), lengthsq(f) = 11.25.
            //   Elite (r = 2.6+0.1 = 2.7, r^2 = 7.29): NOT "start inside"
            //   (11.25 > 7.29). a=36, b=-36, cc=11.25-7.29=3.96,
            //   disc=36^2-4*36*3.96=725.76, sqrt=26.9399,
            //   tEnter=(36-26.9399)/72=0.1258, tExit=(36+26.9399)/72=0.8742
            //   (both solvers — SegmentCircle for gather, SegmentCircleInterval
            //   for accept — run the identical quadratic, so this is what
            //   BOTH read when MobConfigFor is honest).
            //   hEnter = lerp(1.5, 0.5, 0.1258) = 1.3742 -> Classify against
            //   LegsTop 0.6/BodyTop 1.45/HeadTop 1.85 lands in Body (< 1.45).
            //   BodyDamageMult 1f -> 10 dmg -> Hp 58 -> 48.
            //   Chaser/Gunner (r = 0.5+0.1 = 0.6, r^2 = 0.36): cc=11.25-0.36
            //   =10.89, disc=1296-4*36*10.89=-272.16 < 0 -> NO interval
            //   either solver's math. Gather: SegmentCircle returns false,
            //   NO candidate at all (this is exactly what A2 kills). Accept
            //   (only reachable when gather's OWN eliteRadius is honest but
            //   MobConfigFor is wrongly Chaser/Gunner-sized, i.e. B1):
            //   SegmentCircleInterval also returns false -> AcceptCandidate's
            //   fallback tEnter=0,tExit=1 -> hEnter = hStart = 1.5 (UNCHANGED
            //   — this IS the fallback's whole point) -> Classify(1.5) lands
            //   in Head (>= 1.45, < 1.85) -> HeadDamageMult 0f -> 0 dmg ->
            //   Hp stays 58. The fallback and the honest computation now
            //   resolve to DIFFERENT zones because the shot's height
            //   actually changes across the tick — that is the one thing
            //   the old, level fixture never gave them.
            //   (tFloor check: (0.1-1.5)/(-30/30) = 1.4 > 1 — HitFloor never
            //   gathers this tick either way, so it cannot compete.)
            //
            // Mutation: revert MobRadiusFor's `MobType.Elite => cfg.Elite.Radius`
            // to `cfg.Gunner.Radius` (ProjectileSystem.cs) — gather starves,
            // no candidate, Hp stays 58. This is exactly mutation A2,
            // already confirmed in Pack A; this test does not gain a new
            // witness from the rename, it loses a FALSE one (MobConfigFor's
            // Elite case, per the doc above).
            var c = TestConfigs.Open();
            c.Elite = new MobSimConfig
            {
                MaxHp = 58f, Radius = 2.6f,
                LegsTop = 0.60f, BodyTop = 1.45f, HeadTop = 1.85f, // Chaser's own zone bounds
                LegsDamageMult = 1f, BodyDamageMult = 1f, HeadDamageMult = 0f,
            };
            // MaxSpeed/Accel left at 0 (deliberate freeze, same idiom
            // Chaser_Standing_FarPlayer_NoTelegraph in MobAiTests.cs uses):
            // the mob must not wander off (0,0) before the projectile
            // arrives, regardless of which AI branch today's code takes.
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Elite, new float2(0f, 0f));
            var m = w.Mobs[0];
            m.Hp = c.Elite.MaxHp; // known-good starting Hp — coordinator finding #1 above
            w.SetMobForTest(0, m);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(-3f, 1.5f),
                new float2(180f, 0f), height: 1.5f, velZ: -30f, damage: 10f, radius: 0.1f, ttl: 5f);
            for (int i = 0; i < 3; i++) w.Tick(default);
            Assert.Less(w.Mobs[0].Hp, c.Elite.MaxHp);
        }

        /// Stage 3 Task 10 (coordinator finding, Pack B): "archetype ->
        /// body radius" has two homes (see
        /// ProjectileGather_UsesEliteRadius_NotGunnerRadius's own doc above
        /// for the full account of why they are separate and what each
        /// one's own witness is) — ProjectileSystem.MobRadiusFor (gather,
        /// Stage 2's hot-loop decision) and SimulationWorld.MobConfigFor
        /// (AcceptCandidate, MobAiSystem, WaveSystem, SeparationSystem,
        /// VisibilitySystem). Nothing in the type system enforces that the
        /// two stay in sync — this test is that enforcement, DIRECTLY: for
        /// every MobType value, the radius each home reports must be
        /// identical. Mutating either home's own case for any archetype
        /// (Chaser/Gunner included, not just Elite/Director) reddens this
        /// test on that archetype's own line, independent of any projectile
        /// geometry — no fixture design can make it structurally blind the
        /// way the physics-based test above turned out to be.
        [Test]
        public void MobRadiusFor_AgreesWith_MobConfigFor_ForEveryArchetype()
        {
            var c = TestConfigs.Open();
            c.Elite = new MobSimConfig { Radius = 0.65f };
            c.Director = new MobSimConfig { Radius = 0.95f };
            var w = new SimulationWorld(1, c);
            foreach (MobType type in new[]
                     { MobType.Chaser, MobType.Gunner, MobType.Elite, MobType.Director })
            {
                Assert.AreEqual(w.MobConfigFor(type).Radius, ProjectileSystem.MobRadiusFor(type, in c),
                    $"{type}: gather (MobRadiusFor) and MobConfigFor disagree on body radius");
            }
        }

        /// Stage 3 Task 16 (spec §3.7, coordinator R-126, placed here per
        /// spec §4's own EliteAndDirectorTests assignment, not
        /// LootContainerTests.cs): the Director's own drop is a FIXED
        /// rule, never a DropChance read — three tier-3 containers (1-2
        /// items each, TestConfigs' own Id=3 record) plus one separate
        /// tier-4 memory-core container (TestConfigs' own Id=4 record).
        /// TestConfigs.Open() keeps DropChance at Default()'s all-zero,
        /// proving this path never reads it (the Director's own row is
        /// never touched — coordinator R-126). Kind = Cache, Ttl = 0f
        /// (coordinator fix-round Ф3 review A-1 — corrects this task's own
        /// original MobCorpse/180s choice, see SimulationWorld.DamageMob's
        /// own doc for the full account): spec §3.6 names the corpse/
        /// crate/cache trio "not-expiring… there lies what was earned",
        /// and the Director's guaranteed boss drop is exactly that.
        [Test]
        public void Director_DropsExactlyOneMemoryCore()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var pos = new float2(10f, 0f);
            w.SpawnMobForTest(MobType.Director, pos);

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, float2.zero, ownerIndex: 0);

            Assert.AreEqual(4, w.ContainerCount, "the Director's death must produce all four containers");
            int memoryCoreContainers = 0, tierThreeContainers = 0;
            for (int i = 0; i < w.ContainerCount; i++)
            {
                ContainerState c = w.Containers[i];
                Assert.AreEqual(ContainerKind.Cache, c.Kind);
                Assert.AreEqual(pos, c.Pos, "every one of the four must sit at the death position (R-129)");
                Assert.AreEqual(0f, c.Ttl, $"container {i}: a Director drop must never expire (R-129/A-1)");
                byte first = w.ContainerSlotAt(i, 0);
                if (first == 4) // TestConfigs' own Id=4 Trophy tier-4 record
                {
                    Assert.AreEqual(1, c.SlotCount, "the memory-core container must hold exactly one item");
                    memoryCoreContainers++;
                }
                else
                {
                    Assert.AreEqual(3, first, "a tier-3 container must hold TestConfigs' own Id=3 record");
                    Assert.That(c.SlotCount, Is.EqualTo(1).Or.EqualTo(2),
                        $"container {i}: a tier-3 container must hold 1 or 2 items");
                    tierThreeContainers++;
                }
            }
            Assert.AreEqual(1, memoryCoreContainers, "exactly one memory-core container must exist");
            Assert.AreEqual(3, tierThreeContainers, "exactly three tier-3 containers must exist");
        }
    }
}
