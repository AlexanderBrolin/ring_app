using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    public class ConfigTests
    {
        const float Eps = 1e-5f;

        // Stage 3 Task 8: internal, not private — ZoneConfigTests.cs (same
        // assembly, reuse > duplication) builds fixtures off the same seven
        // fresh-SO tuple rather than re-declaring this construction.
        internal static (HeroConfig, WeaponConfig, MobConfig, MobConfig, WaveConfig, ArenaConfig, VisibilityConfig) MakeDefaults()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            var weapon = ScriptableObject.CreateInstance<WeaponConfig>();
            var chaser = ScriptableObject.CreateInstance<MobConfig>();
            var gunner = ScriptableObject.CreateInstance<MobConfig>();
            var wave = ScriptableObject.CreateInstance<WaveConfig>();
            var arena = ScriptableObject.CreateInstance<ArenaConfig>();
            // Stage 2 Task 22: seventh BuildShipped() parameter.
            var visibility = ScriptableObject.CreateInstance<VisibilityConfig>();
            return (hero, weapon, chaser, gunner, wave, arena, visibility);
        }

        /// Stage 3 Task 12 (coordinator finding on mutation M8): the FULL
        /// shipped asset set, and the reason it has to exist is a defect this
        /// file carried the moment Т12 added two assets to the game.
        ///
        /// `MakeDefaults` returns the SEVEN SOs `SimConfigBuilder.Build` took
        /// before this task. Every test that means "the configuration the game
        /// actually ships" kept calling Build with those seven, so
        /// `SimConfig.Elite`/`Director` arrived all-zero — a configuration that
        /// exists nowhere but in this file. That is not cosmetic: the wave
        /// spawn-ring rule (R-55) sizes its forbidden band with
        /// MaxBodyRadius(waveArchetypesOnly), which is max(Chaser, Gunner,
        /// Elite) — 0.8 with the Elite asset, 0.5 without it. Mutation M8
        /// (SpawnRingInset 2 -> 1.5) put the Core spawn ring at 63.5, INSIDE
        /// the real band (63.2, 66.8) and exactly ON the boundary of the
        /// weakened one (63.5, 66.5), where InArcBand's strict `>` reads it as
        /// outside. The rule stayed silent, and the layout tests passed a
        /// mutation aimed straight at them — green because a default had
        /// weakened the rule, not because the layout was sound.
        ///
        /// The seven-argument form stays for the ~45 tests that vary ONE field
        /// to prove ONE rule: those are unit tests of a rule, not statements
        /// about the shipped arena.
        /// Stage 3 Т22 (coordinator R-186): `internal`, and every shipped
        /// section is now an OVERRIDABLE default rather than a fixed one.
        /// SimConfigBuilder.Build's five trailing parameters stopped being
        /// optional in this task — the debt its own MaxBodyRadius doc named Т22
        /// for — so every test that used to hand Build seven assets hands them
        /// to THIS method instead, and gets the other five filled in with what
        /// the game actually ships. A test that wants to vary one of the five
        /// passes it by name and gets the rest shipped, which is exactly what
        /// the old optional-parameter form was being used for, minus its one
        /// dangerous property: a rule could no longer be silently weakened by
        /// an absent section (mutation M8, Т12).
        internal static SimConfig BuildShipped(HeroConfig hero, WeaponConfig weapon, MobConfig chaser,
            MobConfig gunner, WaveConfig wave, ArenaConfig arena, VisibilityConfig visibility,
            MobConfig elite = null, MobConfig director = null, MatchFlowConfig flow = null,
            ItemCatalog items = null, LootConfig loot = null)
        {
            var (shippedElite, shippedDirector) = MakeShippedArchetypes();
            elite ??= shippedElite;
            director ??= shippedDirector;
            // Ф2 review C1: the flow asset belongs here too, and leaving it out
            // was this method's own founding defect repeated one parameter
            // over. `Flow` reaching the simulation as five zeros is exactly the
            // shape mutation M8 exposed for Elite: Т21/Т22 read GateDelaySeconds
            // and ExtractChannelSeconds, and a gate test at GateDelaySeconds = 0
            // would open in the same tick the Director dies and pass while
            // proving nothing (class R-46/R-48).
            flow ??= ScriptableObject.CreateInstance<MatchFlowConfig>();
            // Stage 3 Task 13 (coordinator R-84): the catalog and loot
            // balance sheet join Flow above — the SAME founding defect this
            // method exists to close (mutation M8, Т12) repeats itself one
            // parameter further the day this method stops collecting every
            // asset the game actually ships. Neither needs seeding the way
            // MakeShippedArchetypes does: both are brand-new SO CLASSES
            // whose own C# field initializers already ARE the shipped
            // numbers (ItemCatalog.cs/LootConfig.cs's own doc, same
            // precedent as MatchFlowConfig).
            items ??= ScriptableObject.CreateInstance<ItemCatalog>();
            loot ??= ScriptableObject.CreateInstance<LootConfig>();
            return SimConfigBuilder.Build(hero, weapon, chaser, gunner, wave, arena, visibility,
                elite, director, flow, items, loot);
        }

        /// The two archetype assets Т12 ships, seeded from the shared
        /// TestConfigs baseline. Same discipline the gunner has needed since
        /// Stage 1 and for the same reason: MobEliteConfig/MobDirectorConfig
        /// are assets of the shared MobConfig class, whose C# initializers are
        /// the CHASER's numbers, so a fresh CreateInstance is a chaser and the
        /// archetype numbers have to be put on it. The bootstrap
        /// (StageOneSceneBootstrap.ApplyEliteDefaults/ApplyDirectorDefaults)
        /// seeds the real .asset from its own literals; this is the test side
        /// of the same two-sources split (spec §0).
        internal static (MobConfig elite, MobConfig director) MakeShippedArchetypes()
        {
            SimConfig expected = TestConfigs.Default();
            var elite = ScriptableObject.CreateInstance<MobConfig>();
            var director = ScriptableObject.CreateInstance<MobConfig>();
            SeedMob(elite, in expected.Elite);
            SeedMob(director, in expected.Director);
            return (elite, director);
        }

        /// Copies a MobSimConfig onto a MobConfig asset, field for field — one
        /// home for what used to be a 28-line block inside
        /// Build_DefaultAssets_MatchesTestConfigsBaseline (rule 2). Every field
        /// SimConfigBuilder.ToMobSimConfig reads is set here; if that method
        /// grows a field and this one does not, the baseline test's own
        /// AssertMobEqual is what goes red.
        internal static void SeedMob(MobConfig target, in MobSimConfig source)
        {
            target.MaxSpeed = source.MaxSpeed;
            target.Accel = source.Accel;
            target.Radius = source.Radius;
            target.MaxHp = source.MaxHp;
            target.ContactDamage = source.ContactDamage;
            target.AttackRange = source.AttackRange;
            target.TelegraphSeconds = source.TelegraphSeconds;
            target.AttackCooldown = source.AttackCooldown;
            target.PreferredRange = source.PreferredRange;
            target.RangeTolerance = source.RangeTolerance;
            target.StrafeSpeed = source.StrafeSpeed;
            target.FireInterval = source.FireInterval;
            target.ProjectileSpeed = source.ProjectileSpeed;
            target.ProjectileRadius = source.ProjectileRadius;
            target.ProjectileLifetime = source.ProjectileLifetime;
            target.ProjectileDamage = source.ProjectileDamage;
            target.LeadFactor = source.LeadFactor;
            target.SeparationRadius = source.SeparationRadius;
            target.SeparationStrength = source.SeparationStrength;
            target.AvoidLookahead = source.AvoidLookahead;
            target.AvoidMargin = source.AvoidMargin;
            target.LegsTop = source.LegsTop;
            target.BodyTop = source.BodyTop;
            target.HeadTop = source.HeadTop;
            target.LegsDamageMult = source.LegsDamageMult;
            target.BodyDamageMult = source.BodyDamageMult;
            target.HeadDamageMult = source.HeadDamageMult;
            target.MuzzleHeight = source.MuzzleHeight;
            target.SwingLeadFactor = source.SwingLeadFactor;
            target.SwingLeadMaxMeters = source.SwingLeadMaxMeters;
        }

        /// Builds a SimConfig from a caller-supplied hero and default everything else —
        /// for zone-validation tests (Task 1) that only need to vary Hero fields.
        static SimConfig BuildWith(HeroConfig hero)
        {
            var (_, w, c, g, wv, a, vis) = MakeDefaults();
            return BuildShipped(hero, w, c, g, wv, a, vis);
        }

        [Test]
        public void Build_DefaultAssets_ProducesValidConfig()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.Greater(cfg.Hero.MaxSpeed, 0f);
            Assert.AreEqual(a.Obstacles.Length, cfg.Arena.ObstacleCount);
        }

        [Test]
        public void Build_ObstacleOutsideArena_Throws()
        {
            // Stage 3 Task 12: the position is FIXTURE ARITHMETIC off
            // a.Radius, not the literal 100 it used to be. At Radius 65 that
            // literal put the circle 37 m past the rim; at 113 it lands 11 m
            // INSIDE, and the test would have gone green on an arena that
            // never violated the rule at all (this file's own convention
            // elsewhere — see Validate_WallOverRingSpawnPoint's SpawnPosFor
            // note — and the reason the number is derived here for good).
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = new Vector2(a.Radius, 0f), Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void Build_NegativeSpeed_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            h.MaxSpeed = -1f;
            Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
        }

        [Test]
        public void Build_NegativeBarrierTop_Throws()
        {
            // Stage 2 Task 46: zero is legal — it is the "no modelled top"
            // reading the whole feature defaults to — but a negative height is
            // not a quieter way of saying it, it is a number with no meaning,
            // and the [Range(0, 20)] hint on the SO is never enforced on a
            // value reaching the builder from code or a test fixture (same I3
            // rationale as Arena.MaxPlayers).
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.BarrierTop = -1f;
            Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));

            a.BarrierTop = 0f;
            Assert.DoesNotThrow(() => BuildShipped(h, w, c, g, wv, a, vis),
                "zero means 'no modelled top' and must stay a legal authoring choice");
        }

        [Test]
        public void Build_ObstacleOverSpawnPoint_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = Vector2.zero, Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
        }

        // P-4.5: the SO defaults are the single source of truth for starting balance —
        // they must reproduce TestConfigs.Default() exactly (once the gunner asset's
        // fields are set to the real gunner numbers, since Task 6 only ships the
        // Chaser-shaped MobConfig default; the actual Gunner .asset arrives in Task 7).
        [Test]
        public void Build_DefaultAssets_MatchesTestConfigsBaseline()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var expected = TestConfigs.Default();

            // Stage 3 Task 12: the 28 hand-written assignments that stood here
            // are now SeedMob's job — the same copier MakeShippedArchetypes
            // uses for the Elite and the Director, so all three archetype
            // fixtures stay in step by construction (rule 2).
            SeedMob(g, in expected.Gunner);

            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);

            AssertHeroEqual(expected.Hero, cfg.Hero);
            AssertWeaponEqual(expected.Weapon, cfg.Weapon);
            // Stage 3 Task 2 (errata E-4/A-C3): AmmoStart is the ONE Weapon
            // field where the two number sources disagree on purpose — same
            // documented-deviation category as ArenaConfig.BarrierTop below,
            // so it cannot live inside AssertWeaponEqual. The SO's C# default
            // (120, the real starting magazine) has to reach the builder
            // untouched; the shared TestConfigs baseline deliberately
            // overrides it to 400 so the solo/multiplayer golden scenarios
            // (which hold the trigger for ~245-277 shots over 1000 ticks)
            // never run the magazine dry outside a sanctioned re-pin
            // (TestConfigs.Default()'s own Weapon comment).
            Assert.AreEqual(w.AmmoStart, cfg.Weapon.AmmoStart,
                "WeaponConfig.AmmoStart must reach WeaponSimConfig through the builder");
            Assert.AreEqual(400, expected.Weapon.AmmoStart,
                "the TestConfigs baseline must stay at its ammo-safe fixture value");
            Assert.AreNotEqual(expected.Weapon.AmmoStart, w.AmmoStart,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            AssertMobEqual(expected.Chaser, cfg.Chaser);
            AssertMobEqual(expected.Gunner, cfg.Gunner);
            AssertWaveEqual(expected.Wave, cfg.Wave);
            // Task Т2 (app-ggvz, spec §3.8 Р337) and Task Т6 (app-ggvz,
            // owner decisions К5/Р311): FIVE Wave fields where the two
            // number sources disagree ON PURPOSE, same documented-deviation
            // category as AmmoStart/BarrierTop above and CellsPerMob/
            // DropChance below.
            //
            // Three of them are Т2's own delivery — the shipped SO carries
            // the real per-zone cadence numbers ({20,30,30}s pause,
            // {150,110,10} ceilings, 20s difficulty step), while the shared
            // TestConfigs baseline stays on its own scaled-down fixture
            // (spec Р325: {2,3,3}s, {24,16,8}, 2s) so the 3000-18000-tick
            // DeterminismTests scenarios stay a determinism check, not a
            // load test.
            //
            // The other two — BaseCount and EliteShareOuterGrowth — disagree
            // for a DIFFERENT reason, from Т6: the owner RAISED the real
            // number (BaseCount 4 -> 16, decision К5; EliteShareOuterGrowth
            // 0.02 -> 0.007, decision Р311), and the TestConfigs fixture
            // deliberately stayed pinned at its own OLD values (4, 0.02f)
            // instead of following it up — not because the fixture was ever
            // "scaled down" against these two, but because a fixture that
            // tracked BaseCount up to 16 would turn the golden scenarios
            // into a load test rather than a determinism check, same
            // consequence as the three Т2 fields above for a different
            // cause.
            //
            // AssertWaveEqual above deliberately excludes all five; each
            // gets the same triple form here instead: the real SO reaches
            // the builder untouched, TestConfigs stays at its own pinned
            // number, and the two provably differ.
            // MaxSpawnsPerZonePerTick is NOT here — it agrees on both sides
            // (2 = 2) and is covered by ordinary equality inside
            // AssertWaveEqual instead.
            Assert.AreEqual(wv.BaseCount, cfg.Wave.BaseCount,
                "WaveConfig.BaseCount must reach WaveSimConfig through the builder");
            Assert.AreEqual(4, expected.Wave.BaseCount,
                "the TestConfigs baseline must stay at its own determinism-safe fixture value");
            Assert.AreNotEqual(expected.Wave.BaseCount, cfg.Wave.BaseCount,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(wv.EliteShareOuterGrowth, cfg.Wave.EliteShareOuterGrowth, Eps,
                "WaveConfig.EliteShareOuterGrowth must reach WaveSimConfig through the builder");
            Assert.AreEqual(0.02f, expected.Wave.EliteShareOuterGrowth, Eps,
                "the TestConfigs baseline must stay at its own determinism-safe fixture value");
            Assert.AreNotEqual(expected.Wave.EliteShareOuterGrowth, cfg.Wave.EliteShareOuterGrowth,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            CollectionAssert.AreEqual(wv.WavePauseByZone, cfg.Wave.WavePauseByZone,
                "WaveConfig.WavePauseByZone must reach WaveSimConfig through the builder");
            CollectionAssert.AreEqual(new[] { 2f, 3f, 3f }, expected.Wave.WavePauseByZone,
                "the TestConfigs baseline must stay at its own scaled-down fixture value");
            CollectionAssert.AreNotEqual(expected.Wave.WavePauseByZone, cfg.Wave.WavePauseByZone,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            CollectionAssert.AreEqual(wv.MaxAliveByZone, cfg.Wave.MaxAliveByZone,
                "WaveConfig.MaxAliveByZone must reach WaveSimConfig through the builder");
            CollectionAssert.AreEqual(new[] { 24, 16, 8 }, expected.Wave.MaxAliveByZone,
                "the TestConfigs baseline must stay at its own scaled-down fixture value");
            CollectionAssert.AreNotEqual(expected.Wave.MaxAliveByZone, cfg.Wave.MaxAliveByZone,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(wv.DifficultyStepSeconds, cfg.Wave.DifficultyStepSeconds, Eps,
                "WaveConfig.DifficultyStepSeconds must reach WaveSimConfig through the builder");
            Assert.AreEqual(2f, expected.Wave.DifficultyStepSeconds, Eps,
                "the TestConfigs baseline must stay at its own scaled-down fixture value");
            Assert.AreNotEqual(expected.Wave.DifficultyStepSeconds, cfg.Wave.DifficultyStepSeconds,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            AssertArenaEqual(expected.Arena, cfg.Arena);
            // Stage 2 Task 46 (bd app-r8x): BarrierTop is the ONE arena field
            // where the two number sources disagree on purpose, so it cannot
            // live inside AssertArenaEqual above — and leaving it out silently
            // is exactly how a field drops out of this test's coverage (the
            // Task 4 and Task 16 notes in that method say so about their own
            // fields). Both sides are pinned against their OWN source instead:
            // the SO's C# default has to reach the builder untouched, and the
            // shared test baseline has to stay at "no modelled top" so the
            // golden scenarios never execute the new branch (TestConfigs.
            // DefaultArena's own comment).
            Assert.AreEqual(a.BarrierTop, cfg.Arena.BarrierTop, Eps,
                "ArenaConfig.BarrierTop must reach ArenaSimConfig through the builder");
            Assert.AreEqual(0f, expected.Arena.BarrierTop, Eps,
                "the TestConfigs baseline must stay on 'no modelled top'");
            Assert.AreNotEqual(expected.Arena.BarrierTop, cfg.Arena.BarrierTop,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            // Stage 2 Task 22 (carryover-t22 §2): without this the five
            // VisibilityConfig C# DEFAULTS vs TestConfigs' own baseline are
            // pinned by no test at all — same documented-deviation category as
            // AssertArenaEqual's own Task 4 note below. Phase Ф5 fix-wave
            // (I-4): "C# defaults", not "the .asset" — MakeDefaults above
            // builds every SO with ScriptableObject.CreateInstance, which
            // takes the class's own field initializers and never loads a file
            // from disk. The shipped .asset staying in step with those
            // defaults is a different invariant, checked at phase level, and
            // deliberately not a unit test (spec §0/Р56 keeps the two number
            // sources apart on purpose).
            AssertVisibilityEqual(expected.Visibility, cfg.Visibility);
            AssertFlowEqual(expected.Flow, cfg.Flow);
            // Coordinator fix-round (Ф3 review I1 — third occurrence of this
            // class of gap this stage, Ф2-C1/I1 the first two): Loot/Items
            // silently dropped out of this test's own coverage since Т13.
            // LootConfig/ItemCatalog are not top-level MakeDefaults()
            // parameters (BuildShipped creates its own instances internally,
            // same as Flow/Elite/Director) — a FRESH instance read here for
            // its own C# defaults is the same "real SO, not the builder's
            // copy" source AmmoStart/BarrierTop's own `w`/`a` parameters
            // already are, just not thread through a return value.
            var realLoot = ScriptableObject.CreateInstance<LootConfig>();
            var realItems = ScriptableObject.CreateInstance<ItemCatalog>();
            // AssertLootEqual covers the EIGHT fields that genuinely mirror
            // LootConfig.cs's own C# defaults; the other SEVEN are
            // deliberately golden-safety-zeroed in TestConfigs (same
            // documented-deviation category as AmmoStart/BarrierTop right
            // above — reviewer B's own "15 полей блочно" framing was wider
            // than the fact: a blanket comparator would fail on those seven
            // immediately). Each of the seven gets the SAME triple form
            // AmmoStart/BarrierTop already use: the real SO reaches the
            // builder untouched, TestConfigs stays at its own deliberate
            // zero, and the two provably differ.
            AssertLootEqual(expected.Loot, cfg.Loot);
            CollectionAssert.AreEqual(realLoot.CellsPerMob, cfg.Loot.CellsPerMob,
                "LootConfig.CellsPerMob must reach LootSimConfig through the builder");
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, expected.Loot.CellsPerMob,
                "the TestConfigs baseline must stay at golden-safety zero");
            CollectionAssert.AreNotEqual(expected.Loot.CellsPerMob, cfg.Loot.CellsPerMob,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            CollectionAssert.AreEqual(realLoot.DropChance, cfg.Loot.DropChance,
                "LootConfig.DropChance must reach LootSimConfig through the builder");
            bool realDropChanceHasNonzero = false;
            foreach (float share in cfg.Loot.DropChance) if (share > 0f) realDropChanceHasNonzero = true;
            Assert.IsTrue(realDropChanceHasNonzero,
                "premise: LootConfig.DropChance's own real shares must actually be non-zero, or " +
                "the golden-safety comparison right below proves nothing");
            foreach (float share in expected.Loot.DropChance)
                Assert.AreEqual(0f, share, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreEqual(realLoot.CrateCount, cfg.Loot.CrateCount,
                "LootConfig.CrateCount must reach LootSimConfig through the builder");
            Assert.AreEqual(0, expected.Loot.CrateCount, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreNotEqual(expected.Loot.CrateCount, cfg.Loot.CrateCount,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(realLoot.CacheCountMiddle, cfg.Loot.CacheCountMiddle,
                "LootConfig.CacheCountMiddle must reach LootSimConfig through the builder");
            Assert.AreEqual(0, expected.Loot.CacheCountMiddle, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreNotEqual(expected.Loot.CacheCountMiddle, cfg.Loot.CacheCountMiddle,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(realLoot.CacheCountCore, cfg.Loot.CacheCountCore,
                "LootConfig.CacheCountCore must reach LootSimConfig through the builder");
            Assert.AreEqual(0, expected.Loot.CacheCountCore, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreNotEqual(expected.Loot.CacheCountCore, cfg.Loot.CacheCountCore,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(realLoot.RepairKitChance, cfg.Loot.RepairKitChance, Eps,
                "LootConfig.RepairKitChance must reach LootSimConfig through the builder");
            Assert.AreEqual(0f, expected.Loot.RepairKitChance, Eps, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreNotEqual(expected.Loot.RepairKitChance, cfg.Loot.RepairKitChance,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            Assert.AreEqual(realLoot.CorpseCellFraction, cfg.Loot.CorpseCellFraction, Eps,
                "LootConfig.CorpseCellFraction must reach LootSimConfig through the builder");
            Assert.AreEqual(0f, expected.Loot.CorpseCellFraction, Eps, "the TestConfigs baseline must stay at golden-safety zero");
            Assert.AreNotEqual(expected.Loot.CorpseCellFraction, cfg.Loot.CorpseCellFraction,
                "and the divergence is deliberate — if these ever agree, one of the two "
                + "sources moved and the reason above no longer holds");
            AssertItemsEqual(realItems.Items, cfg.Items);

            // The chaser/gunner archetypes must land in the matching SimConfig slot,
            // not get swapped by the builder's mapping.
            Assert.AreEqual(expected.Chaser.FireInterval, cfg.Chaser.FireInterval, Eps);
            Assert.AreEqual(expected.Gunner.FireInterval, cfg.Gunner.FireInterval, Eps);
            Assert.AreNotEqual(cfg.Chaser.FireInterval, cfg.Gunner.FireInterval);
        }

        [Test]
        public void Validate_ZoneOrderViolated_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.LegsTop = 1.0f; hero.BodyTop = 0.5f; // zone order violated
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("LegsTop"));
        }

        [Test]
        public void Validate_SlideProfileAboveGunnerMuzzle_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            // NB (QA2/QD3): a fresh MobConfig has ProjectileRadius = 0 (chaser
            // defaults), so 1.0 is used: 1.0 + 0 >= MuzzleHeight(0.95) — rule D5
            // violated, while 1.0 <= Hero.BodyTop (1.35) — the other rules stay quiet.
            hero.SlideProfileTop = 1.0f;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("SlideProfileTop"));
        }

        [Test]
        public void Validate_ZeroStaminaRegen_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.StaminaRegenPerSec = 0f;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("StaminaRegenPerSec"));
        }

        [Test]
        public void Validate_AimFracNotAboveSlideFrac_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.AimMoveSpeedFrac = hero.SlideMinSpeedFrac; // equality is also a violation (strict >)
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("AimMoveSpeedFrac"));
        }

        [Test]
        public void Validate_SwingLeadFactorOutOfRange_Throws()
        {
            // I3 (final review wave, app-n6g): SwingLeadFactor is spec-mandated
            // to stay within [0,2] per archetype (MobConfig's own [Range(0f,
            // 2f)] Inspector hint — never enforced outside the Editor UI, so
            // SimConfigBuilder must reject it too) but ValidateMob never
            // checked it.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            c.SwingLeadFactor = 2.5f; // outside [0, 2]
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("SwingLeadFactor"));
        }

        [Test]
        public void Validate_SwingLeadMaxMetersNegative_Throws()
        {
            // I3 (final review wave, app-n6g): SwingLeadMaxMeters must stay
            // non-negative per archetype — ValidateMob never checked it.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            c.SwingLeadMaxMeters = -1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("SwingLeadMaxMeters"));
        }

        [Test]
        public void Validate_LinkRefundNotBelowMinCost_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            // Equality is also a violation (strict <) — В1 fix-wave 3 economy rework.
            hero.LinkRefund = math.min(hero.DashStaminaCost, hero.SlideStaminaCost);
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("LinkRefund"));
        }

        [Test]
        public void Validate_EdgeRequestMinTicksNegative_Throws()
        {
            // Fix-round 1 I-2: written while EdgeRequestMinTicks was still
            // data-only, so that a validation-line regression could not
            // silently let a negative value reach the future gate. Stage 2
            // Task 10 built that gate (PlayerMovementSystem.Update), so the
            // guard is now protecting a live consumer — same convention every
            // other [Range]-guarded field on this class gets (e.g.
            // Validate_SwingLeadMaxMetersNegative_Throws below).
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.EdgeRequestMinTicks = -1;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("EdgeRequestMinTicks"));
        }

        [Test]
        public void Validate_EdgeRequestMinTicksAboveRange_Throws()
        {
            // app-zx8 (spec §6e, decision "a"): [Range(0,15)] on HeroConfig is an
            // Editor-only Inspector hint — a value reaching the builder from
            // code/JSON/test fixtures is not bounded by it at all, only by the
            // old ReqNonNegative(>= 0) check. Mirrors the upper-bound precedent
            // set for Arena.MaxPlayers in Task 4.
            // Mutation-testing note (app-zx8 Step 6): at the shipped default
            // link windows (~0.25-0.32s) every value above 15 ticks is already
            // >= 0.5s, so it also trips the cross-check below — a test using
            // default windows could never isolate this rule from that one
            // (confirmed by mutation: dropping the range check alone left this
            // test passing). Widening both link windows to their own
            // [Range(0,1)] ceiling first pushes the cross-check threshold
            // (~30 ticks) safely above 16, so only the range rule can fire.
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.LinkWindowSeconds = 1f; // own [Range(0,1)] ceiling
            hero.PostDashSlideWindow = 1f; // own [Range(0,1)] ceiling
            hero.EdgeRequestMinTicks = 16; // outside [0, 15]; still well under either widened window
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("EdgeRequestMinTicks"));
            Assert.That(ex.Message, Does.Not.Contain("LinkWindowSeconds")); // must be the range rule, not the cross-check
        }

        [Test]
        public void Validate_EdgeRequestMinTicksEatsLinkWindow_Throws()
        {
            // app-zx8: a value inside the plain [0,15] range can still be tall
            // enough in real time to swallow the dash<->slide link windows
            // whole (task-zx8-brief.md: at the shipped .asset numbers,
            // EdgeRequestMinTicks >= 8 eats the legal link entirely). The
            // smallest tick count whose duration reaches the narrower window
            // is computed from hero's own fixture fields plus
            // SimulationWorld.TickDt — never a literal copied from the .asset.
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            float minLinkWindow = math.min(hero.LinkWindowSeconds, hero.PostDashSlideWindow);
            hero.EdgeRequestMinTicks = (int)math.ceil(minLinkWindow / SimulationWorld.TickDt);
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("EdgeRequestMinTicks"));
            Assert.That(ex.Message, Does.Contain("LinkWindowSeconds"));
        }

        [Test]
        public void Validate_EdgeRequestMinTicksDefault_PassesRangeAndLinkCheck()
        {
            // app-zx8: the shipped default (3 ticks = 0.1s) must clear both the
            // new upper-bound range check and the new cross-check against the
            // link windows — this is the "still works" half of the side-quest.
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            Assert.DoesNotThrow(() => BuildWith(hero));
        }

        [Test]
        public void Validate_PickupRadiusZero_Throws()
        {
            // Stage 3 Task 3: same ReqPositive convention as every other
            // radius/distance field on this class (e.g. Validate_
            // ZeroStaminaRegen_Throws above).
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.PickupRadius = 0f;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("PickupRadius"));
        }

        // ------------------------------------------------------------------
        // Stage 2 Task 16 (spec §3.4/§3.15): the shipped arena layout and the
        // wall validation rules that guard a future tuning pass over it.
        // ------------------------------------------------------------------

        /// Every candidate spawn point for a full lobby, in ring order — the
        /// same Geometry.SpawnPosFor the world itself uses, never a restated
        /// copy of the trigonometry.
        static float2[] SpawnPoints(in ArenaSimConfig arena)
        {
            var pts = new float2[arena.MaxPlayers];
            for (int i = 0; i < arena.MaxPlayers; i++)
                pts[i] = Geometry.SpawnPosFor(i, arena.MaxPlayers, in arena);
            return pts;
        }

        /// How many of WaveSystem's fixed fallback ring slots a body of
        /// `bodyRadius` could still occupy — mirrors WaveSystem.TryFindSpawnPos'
        /// RNG-free grid and IsValidSpawn's geometry half (obstacles + walls),
        /// which is exactly what "the wave spawn ring is not locked" means.
        /// Stage 3 Task 12: `ringRadiusOrNaN` is a TRAILING OPTIONAL parameter
        /// (Global Constraints) — left out, the ring is the arena-wide one
        /// SimConfigBuilder's own "the whole spawn ring is locked" rule walks;
        /// supplied, it is one zone's own wave spawn ring
        /// (Geometry.ZoneSpawnRingRadius). The ARC loop is new in the same
        /// task and unconditional: it mirrors WaveSystem.IsValidSpawn, which
        /// has rejected candidates inside an arc body since Т9, and it cannot
        /// change the pre-Т12 caller's answer — that ring sits at
        /// Radius - SpawnRingInset, outside every arc band by construction
        /// (Т12's own layout arithmetic: 17.2 m of margin at the shipped
        /// numbers).
        /// Stage 3 Task 15 (coordinator R-102/R-111): the BLOCKED check below
        /// delegates to SpawnPlacement.GeometryBlocked (doorsPassable: true,
        /// same door policy as WaveSystem.IsValidSpawn/SimConfigBuilder.
        /// RingSlotBlocked) — the third of the three copies R-111's own
        /// ledger names collapsing onto the one shared home. The grid-ANGLE
        /// formula (coordinator R-115, a SEPARATE finding from R-111 above —
        /// it is bit-sensitive per R-103, and three copies of it had
        /// survived R-111's own first pass) now comes from
        /// SpawnPlacement.FallbackSlotPos too, not restated a third time
        /// here — that function's own doc explains why it is its OWN home
        /// rather than folded into SpawnPlacement.TryFind: this method never
        /// draws from an RNG stream at all, it just counts free FIXED slots,
        /// which is not a search.
        static int FreeRingSlots(in SimConfig cfg, float bodyRadius, float ringRadiusOrNaN = float.NaN)
        {
            float ringRadius = float.IsNaN(ringRadiusOrNaN)
                ? cfg.Arena.Radius - cfg.Wave.SpawnRingInset
                : ringRadiusOrNaN;
            int free = 0;
            for (int i = 0; i < cfg.Wave.FallbackSlots; i++)
            {
                // Coordinator R-115: the slot angle formula is now the one
                // shared SpawnPlacement.FallbackSlotPos home, not a third
                // copy — see that method's own doc for why it is bit-sensitive.
                float2 p = SpawnPlacement.FallbackSlotPos(ringRadius, i, cfg.Wave.FallbackSlots);
                if (!SpawnPlacement.GeometryBlocked(in cfg.Arena, p, bodyRadius, doorsPassable: true))
                    free++;
            }
            return free;
        }

        [Test]
        public void Layout_SomeSpawnPairHasNoLineOfSight()
        {
            // Spec §3.4 requirement (a): at least one pair of spawn points must
            // be unable to see each other from the very first tick, so a
            // three-way match does not open with everyone in everyone's sights.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            float2[] pts = SpawnPoints(in cfg.Arena);
            Assert.GreaterOrEqual(pts.Length, 2, "a full lobby needs at least two spawn points");

            bool anyBlocked = false;
            for (int i = 0; i < pts.Length && !anyBlocked; i++)
                for (int j = i + 1; j < pts.Length && !anyBlocked; j++)
                    anyBlocked = !Ring.Simulation.AI.Targeting.HasLineOfFire(pts[i], pts[j], 0f, in cfg.Arena);

            Assert.IsTrue(anyBlocked,
                "no pair of spawn points is out of line of sight — spec §3.4 (a) unmet");
        }

        [Test]
        public void Layout_P0P1PairBlockedSpecificallyByZoneWall()
        {
            // Fixwave Ф3 item 4 pinned this pair against the INTERIOR WALLS,
            // because at Radius 65 spec §3.15's "one lone wall breaking line
            // of sight between the P0 and P1 spawn points" was the only thing
            // standing between them. Stage 3 Task 12 moves the spawn ring from
            // 52 m to 103.96 m and the walls stay where they were: no stadium
            // comes near that chord any more (the nearest, (2,24)-(2,44),
            // misses it by 14.9 m), and what breaks the pair now is the zone
            // wall the same task ships. Owner decision R-74: the test follows
            // its subject and is RENAMED after it (lesson 277 — a name must
            // promise exactly what the body asserts), keeping both the §3.4(a)
            // requirement and the sensitivity arm the old test was built
            // around. The arm is what makes this more than a tautology:
            // removing every zone wall, and nothing else, restores visibility,
            // so the block provably belongs to the arc and not to a circle or
            // a stadium that happens to sit nearby.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfgWithZones = BuildShipped(h, w, c, g, wv, a, vis);
            float2[] ptsWithZones = SpawnPoints(in cfgWithZones.Arena);
            Assert.IsFalse(
                Ring.Simulation.AI.Targeting.HasLineOfFire(ptsWithZones[0], ptsWithZones[1], 0f,
                    in cfgWithZones.Arena),
                "P0-P1 must be blocked with the shipped zone walls in place");

            a.ZoneWallCount = 0;
            a.ZoneWallRadius = System.Array.Empty<float>();
            a.ZoneWallHalfWidth = System.Array.Empty<float>();
            a.ZoneWallDoorStart = System.Array.Empty<int>();
            a.ZoneWallDoorCount = System.Array.Empty<int>();
            a.DoorCenterRad = System.Array.Empty<float>();
            a.DoorFreeWidth = System.Array.Empty<float>();
            SimConfig cfgNoZones = BuildShipped(h, w, c, g, wv, a, vis);
            float2[] ptsNoZones = SpawnPoints(in cfgNoZones.Arena);
            Assert.IsTrue(
                Ring.Simulation.AI.Targeting.HasLineOfFire(ptsNoZones[0], ptsNoZones[1], 0f,
                    in cfgNoZones.Arena),
                "removing every zone wall must restore P0-P1 visibility — the block must come " +
                "from an arc, not from a circle obstacle or an interior wall");
        }

        [Test]
        public void Layout_HasCorridorAtLeast20mLongWithSixMetreGap()
        {
            // Spec §3.4 requirement (b): a corridor is a PAIR of parallel walls
            // — running alongside each other for >= 20 m, with a free passage
            // >= 6 m between their inner faces. The length that matters is the
            // SHARED span, not either wall's own length (see the overlap
            // computation below).
            const float MinCorridorLength = 20f;
            const float MinCorridorGap = 6f;

            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);

            bool found = false;
            for (int i = 0; i < cfg.Arena.WallCount && !found; i++)
            {
                float2 axisI = cfg.Arena.WallB[i] - cfg.Arena.WallA[i];
                float lenI = math.length(axisI);
                if (lenI < 1e-4f) continue;
                float2 dirI = axisI / lenI;

                for (int j = i + 1; j < cfg.Arena.WallCount && !found; j++)
                {
                    float2 axisJ = cfg.Arena.WallB[j] - cfg.Arena.WallA[j];
                    float lenJ = math.length(axisJ);
                    if (lenJ < 1e-4f) continue;
                    float2 dirJ = axisJ / lenJ;

                    // Parallel (either orientation): zero 2D cross product.
                    if (math.abs(dirI.x * dirJ.y - dirI.y * dirJ.x) > 1e-3f) continue;

                    // Perpendicular distance between the two axes, minus what the
                    // two half-widths eat = the free passage a body can walk down.
                    float2 offset = cfg.Arena.WallA[j] - cfg.Arena.WallA[i];
                    float separation = math.abs(offset.x * dirI.y - offset.y * dirI.x);
                    float gap = separation - (cfg.Arena.WallHalfWidth[i] + cfg.Arena.WallHalfWidth[j]);

                    // Longitudinal overlap (review of Stage 2 Task 16, I-2): parallel
                    // and 6 m apart is not a corridor unless the two walls actually
                    // run ALONGSIDE each other. Without this, wall 1 (x in [-28,-8],
                    // y = 10) and wall 3 (x in [12,34], y = -6) — one from each of
                    // the two real corridors — satisfied every other clause, so the
                    // test stayed green even with both partner walls deleted, i.e.
                    // spec §3.4 (b) was not pinned at all. Project all four ends onto
                    // dirI and require the shared span to carry the corridor itself.
                    float baseI = math.dot(cfg.Arena.WallA[i], dirI);
                    float sIa = math.dot(cfg.Arena.WallA[i], dirI) - baseI;
                    float sIb = math.dot(cfg.Arena.WallB[i], dirI) - baseI;
                    float sJa = math.dot(cfg.Arena.WallA[j], dirI) - baseI;
                    float sJb = math.dot(cfg.Arena.WallB[j], dirI) - baseI;
                    float overlap = math.min(math.max(sIa, sIb), math.max(sJa, sJb))
                        - math.max(math.min(sIa, sIb), math.min(sJa, sJb));

                    if (overlap >= MinCorridorLength - Eps && gap >= MinCorridorGap - Eps)
                        found = true;
                }
            }

            Assert.IsTrue(found,
                $"no pair of parallel walls forms a corridor >= {MinCorridorLength} m long with a " +
                $">= {MinCorridorGap} m free passage — spec §3.4 (b) unmet");
        }

        [Test]
        public void Layout_WaveSpawnRing_NotFullyBlocked()
        {
            // Spec §3.4 requirement (c). Meaningful only once the layout
            // actually carries walls — an arena with none passes this trivially,
            // so the wall count is asserted first.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.Greater(cfg.Arena.WallCount, 0,
                "the shipped layout must carry interior walls for this requirement to mean anything");

            float bodyRadius = math.max(cfg.Chaser.Radius, cfg.Gunner.Radius);
            Assert.Greater(FreeRingSlots(in cfg, bodyRadius), 0,
                "the wave spawn ring is fully blocked — no wave could ever spawn");
        }

        [Test]
        public void Validate_WallZeroHalfWidth_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(-5f, 0f), B = new Vector2(5f, 0f), HalfWidth = 0f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("HalfWidth"));
        }

        [Test]
        public void Validate_WallZeroLength_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(5f, 5f), B = new Vector2(5f, 5f), HalfWidth = 0.8f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("zero length"));
        }

        [Test]
        public void Validate_WallNearZeroLength_Throws()
        {
            // Fixwave Ф3 item 5: the OLD `length <= 0f` check let a wall this
            // short (2e-7 m, comfortably above 0 but far below Geometry's own
            // 1e-6 degenerate-axis threshold) pass validation, then silently
            // behave like a circle/degenerate axis at runtime everywhere
            // Geometry consumes it (SegmentStadium delegates to SegmentCircle;
            // ClosestPointOnSegment collapses to `dir`). The fix mirrors
            // Geometry's own `math.lengthsq(axis) < 1e-12f` cutoff exactly, so
            // this fixture (length 2e-7, lengthsq 4e-14 < 1e-12) must now be
            // rejected too.
            //
            // B.x is the literal 2e-7f, NOT `5f + 2e-7f`: float32's ULP at
            // magnitude 5 is ~4.8e-7 — bigger than 2e-7 — so adding it to 5f
            // would round straight back down to 5f (catastrophic
            // cancellation), silently collapsing this fixture to the SAME
            // exact-zero-length case Validate_WallZeroLength_Throws already
            // covers and proving nothing extra. Anchoring B at 0 instead
            // keeps the literal exact.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(0f, 5f), B = new Vector2(2e-7f, 5f), HalfWidth = 0.8f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("zero length"));
        }

        [Test]
        public void Validate_WallTooCloseToArenaRim_Throws()
        {
            // carryover-t16 item 7c: "inside Radius - HalfWidth" is NOT enough.
            // A wall whose end sits closer to the rim than a body's own diameter
            // leaves a pocket where PushOutOfStadium and ClampInsideRing fight
            // each other forever at iterations: 1 — a soft-lock. The rule the
            // builder enforces is
            //   max(|A|,|B|) + HalfWidth + bodyRadius + Skin <= Radius - bodyRadius,
            //   where bodyRadius = max(Hero.Radius, Chaser.Radius, Gunner.Radius)
            //   (Validate_WallRimRuleUsesLargestBody_NotJustHero below pins that
            //   specifically), so this fixture places an end EXACTLY on the OLD
            // naive bound (Radius - HalfWidth, no body-radius margin at all —
            // what the pre-carryover-t16 wording would have accepted) and
            // expects a rejection.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float halfWidth = 0.8f;
            float rimEnd = a.Radius - halfWidth; // passes "inside Radius - HalfWidth", fails 7c
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(rimEnd - 10f, 0f), B = new Vector2(rimEnd, 0f), HalfWidth = halfWidth } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("arena rim"));
        }

        [Test]
        public void Validate_WallRimRuleUsesLargestBody_NotJustHero()
        {
            // Review of Stage 2 Task 16, I-1: the rim rule must clear the LARGEST
            // body that can be wedged there, not the hero. A mob is wider than the
            // hero (MobConfig.Radius 0.5 against HeroConfig.Radius 0.45), so a
            // hero-only rule leaves a band where a wall passes validation and a mob
            // driven into it by separation still soft-locks. This fixture places a
            // wall end inside that band: it clears the hero-sized rule and fails the
            // mob-sized one, so it must be rejected. Expressed as fixture arithmetic
            // — no literal reproduces the builder's formula.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float halfWidth = 0.8f;
            float heroBound = a.Radius - h.Radius - halfWidth - h.Radius;   // hero-sized rule
            float mobRadius = math.max(c.Radius, g.Radius);
            float mobBound = a.Radius - mobRadius - halfWidth - mobRadius;  // mob-sized rule
            Assert.Greater(heroBound, mobBound,
                "fixture premise: the mob is the wider body, so its bound is the tighter one");
            float farEnd = 0.5f * (heroBound + mobBound); // strictly between the two bounds

            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(farEnd - 10f, 0f), B = new Vector2(farEnd, 0f), HalfWidth = halfWidth } };

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("arena rim"));
        }

        [Test]
        public void Validate_WallOverPlayerSpawn_Throws()
        {
            // Same contract the obstacle loop already has: every candidate
            // spawn point (every ring size from 1 up to MaxPlayers) must stay
            // clear. This wall straddles the SOLO lobby's own point.
            //
            // It used to be laid through the arena center, which was the solo
            // spawn until Stage 3 Ф5-0 (owner decision R-173) and is a spawn
            // point no more — a wall there now proves nothing about spawns at
            // all (it would trip the portal-clearance rule instead, the
            // extraction gate standing at (0,0), spec §3.15). Position is
            // FIXTURE ARITHMETIC off the builder's own formula, never a
            // literal — the same convention
            // Validate_WallOverRingSpawnPoint_NotJustTheSoloPoint_Throws
            // below follows.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var arenaForSpawn = new ArenaSimConfig { Radius = a.Radius, PlayerSpawnRingFrac = a.PlayerSpawnRingFrac };
            float2 solo = Geometry.SpawnPosFor(0, 1, in arenaForSpawn);
            a.Walls = new[] { new ArenaConfig.Wall
            {
                A = new Vector2(solo.x - 5f, solo.y),
                B = new Vector2(solo.x + 5f, solo.y),
                HalfWidth = 0.8f
            } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ring 1/point 0"));
        }

        [Test]
        public void Validate_WallOverRingSpawnPoint_NotJustTheSoloPoint_Throws()
        {
            // Fixwave Ф3 item 2: Validate_WallOverPlayerSpawn_Throws above
            // covers the SOLO lobby's own point (the n=1 ring point since
            // Stage 3 Ф5-0, the arena center before it), so on its own it
            // would leave a mutant that stops the loop after the first ring
            // size green against every existing fixture. This wall straddles
            // the P1 spawn point of a full 3-player lobby (Geometry.
            // SpawnPosFor(1, 3, ...) — the same formula the builder itself
            // uses, not a restated literal), which no other ring size shares,
            // so only a loop that really walks n up to MaxPlayers can catch
            // it.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var arenaForSpawn = new ArenaSimConfig { Radius = a.Radius, PlayerSpawnRingFrac = a.PlayerSpawnRingFrac };
            float2 spawnP1 = Geometry.SpawnPosFor(1, 3, in arenaForSpawn);

            a.Walls = new[] { new ArenaConfig.Wall
            {
                A = new Vector2(spawnP1.x - 5f, spawnP1.y),
                B = new Vector2(spawnP1.x + 5f, spawnP1.y),
                HalfWidth = 0.8f
            } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ring 3/point 1"));
        }

        [Test]
        public void Validate_WallsLockTheWholeWaveSpawnRing_Throws()
        {
            // A single wall thick enough to swallow the entire spawn ring: no
            // wave could ever place a mob, and the director would spin on
            // undischargeable debt for the whole match.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.Obstacles = System.Array.Empty<ArenaConfig.Obstacle>();
            float ringRadius = a.Radius - wv.SpawnRingInset;
            a.Walls = new[] { new ArenaConfig.Wall
            {
                A = new Vector2(0f, 0.5f),
                B = new Vector2(0f, -0.5f),
                HalfWidth = ringRadius + 5f
            } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("spawn ring"));
        }

        [Test]
        public void Validate_PerPlayerCountFracNegative_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            wv.PerPlayerCountFrac = -0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("PerPlayerCountFrac"));
        }

        [Test]
        public void Validate_InventoryCapacityZero_Throws()
        {
            // Stage 3 Task 4 (errata E-6 D-I8): same ReqPositive convention
            // as every other per-match capacity number on this class (e.g.
            // Validate_MaxPickupsZero_Throws below).
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.InventoryCapacity = 0;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("InventoryCapacity"));
        }

        [Test]
        public void Validate_MaxInventoryItemsZero_Throws()
        {
            // Stage 3 Task 4: MaxInventoryItems sizes SimulationWorld's
            // per-player Loot.Inventory backing array directly
            // (`new byte[maxItems]`) — unlike a mere magnitude, a
            // non-positive value here would not just refuse every add, it
            // would leave a permanently zero-capacity backpack (0) or throw
            // an opaque exception out of the array allocation itself
            // (negative) instead of this clean, named validation error —
            // the same reasoning InventoryCapacity's own test states for
            // its half of errata E-6 D-I8's "if you deem it necessary" clause.
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.MaxInventoryItems = 0;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("MaxInventoryItems"));
        }

        [Test]
        public void Validate_MaxPickupsZero_Throws()
        {
            // Stage 3 Task 3: same ReqPositive convention as every other
            // per-match entity cap on this class (MaxMobs/MaxProjectiles/
            // MaxEventsPerFrame — no dedicated test exists for those three
            // today, this is the first of Arena's caps to get one).
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.MaxPickups = 0;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("MaxPickups"));
        }

        // ------------------------------------------------------------------
        // Stage 3 Task 2's own three validations (spec Р261, errata E-6 D-I8),
        // added in the Ф1 fix-round (review A-I2 / B-I-3): they shipped with
        // the four Stage 3 neighbors above but without a single test between
        // them, so all three branches had never been observed failing. A sign
        // slip in any of them (`>` for `>=`, `<=` for `<`) is silent — the
        // compiler cannot object and nothing else looks — and the cost is paid
        // by whoever authors the `.asset`: an AmmoStart above AmmoMax seeds
        // every player above the ceiling ApplyConfig otherwise enforces, and
        // an EmergencyFireInterval at or below FireInterval inverts С10
        // outright, making the dry weapon the FASTER one.
        // ------------------------------------------------------------------

        [Test]
        public void Validate_ShotsPerCellZero_Throws()
        {
            // Same ReqPositive convention as every other conversion factor on
            // this class; zero would make a picked-up cell worth no shots at
            // all, i.e. an economy with no income (spec §3.6).
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            w.ShotsPerCell = 0;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("ShotsPerCell"));
        }

        [Test]
        public void Validate_AmmoStartAboveAmmoMax_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            w.AmmoStart = w.AmmoMax + 1;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("AmmoStart"));

            // The boundary itself stays legal, and saying so pins WHICH
            // comparison this is: "start the match on a full magazine" is a
            // legitimate authoring choice, so the rule is `>`, not `>=`.
            w.AmmoStart = w.AmmoMax;
            Assert.DoesNotThrow(() => BuildShipped(h, w, c, g, wv, a, vis),
                "AmmoStart == AmmoMax is a full starting magazine, not a misconfiguration");
        }

        [Test]
        public void Validate_EmergencyFireIntervalNotAboveFireInterval_Throws()
        {
            // EQUAL is the subject, not merely "shorter": the rule is `<=`,
            // because an emergency interval equal to the normal one means the
            // synthesis costs nothing at all and С10's whole DPS penalty
            // (Р226: the frequency IS the penalty, there is no damage
            // multiplier) quietly disappears.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            w.EmergencyFireInterval = w.FireInterval;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("EmergencyFireInterval"));

            // And the inversion — the emergency mode outrunning normal fire —
            // is refused too, which is the failure an authoring slip actually
            // produces.
            w.EmergencyFireInterval = w.FireInterval * 0.5f;
            Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis),
                "a dry weapon must never fire FASTER than a loaded one");
        }

        // ------------------------------------------------------------------
        // Stage 2 Task 22 (spec §3.15/Р72; carryover-t22 §1): visibility
        // filter invariants. Each [Range]-bounded field gets two tests, one
        // per side of ReqInRange's `!minOk || value > max` — same convention
        // Validate_EdgeRequestMinTicksNegative_Throws /
        // Validate_EdgeRequestMinTicksAboveRange_Throws set for the floor and
        // ceiling of a single ReqInRange call. Where a fixture would
        // coincidentally also trip the HearRadius >= SightRadius cross-check,
        // the other field is pinned just far enough to keep that check quiet
        // (noted per-test below) so each test isolates exactly one branch.
        // ------------------------------------------------------------------

        [Test]
        public void Validate_VisibilitySightRadiusZero_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.SightRadius = 0f; // floor is exclusive: > 0, not >= 0
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.SightRadius"));
        }

        [Test]
        public void Validate_VisibilitySightRadiusAboveRange_Throws()
        {
            // Mirrors VisibilityConfig's own [Range(5f, 150f)] ceiling — never
            // enforced outside the Editor UI (Р115 precedent). HearRadius is
            // raised alongside SightRadius so HearRadius >= SightRadius stays
            // satisfied and only the ceiling branch fires.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.SightRadius = 151f;
            vis.HearRadius = vis.SightRadius;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.SightRadius"));
        }

        [Test]
        public void Validate_VisibilityHearRadiusBelowSightRadius_Throws()
        {
            // Otherwise a source would count as heard before it counts as seen
            // — carryover-t22 §1's own rationale for the rule.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.HearRadius = vis.SightRadius - 1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            // Phase Ф5 fix-wave (I-2): the two separate Does.Contain checks
            // this used to make were satisfied by the WRONG error just as
            // well as by the right one — "Visibility.SightRadius" appears in
            // that field's own range message too, and "Visibility.HearRadius"
            // in HearRadius's. Assert the cross-check's own distinctive
            // sentence instead, so only the rule under test can satisfy it.
            Assert.That(ex.Message,
                Does.Contain("Visibility.HearRadius must be >= Visibility.SightRadius"),
                "the CROSS-CHECK must be what fires here — not either field's own range message, "
                + "which would mention the same two names and prove nothing");
        }

        [Test]
        public void Validate_VisibilityHearRadiusAboveRange_Throws()
        {
            // Mirrors VisibilityConfig's own [Range(5f, 200f)] ceiling. Default
            // SightRadius (45) stays well under 201, so the cross-check above
            // stays quiet and only the ceiling branch fires.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.HearRadius = 201f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.HearRadius"));
        }

        [Test]
        public void Validate_VisibilityExitHysteresisNegative_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.ExitHysteresis = -1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.ExitHysteresis"));
        }

        [Test]
        public void Validate_VisibilityExitHysteresisAboveRange_Throws()
        {
            // Mirrors VisibilityConfig's own [Range(0f, 20f)] ceiling.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.ExitHysteresis = 21f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.ExitHysteresis"));
        }

        [Test]
        public void Validate_VisibilityHearPositionGridMetersNegative_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.HearPositionGridMeters = -1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.HearPositionGridMeters"));
        }

        [Test]
        public void Validate_VisibilityHearPositionGridMetersAboveRange_Throws()
        {
            // Mirrors VisibilityConfig's own [Range(0f, 10f)] ceiling.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.HearPositionGridMeters = 11f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.HearPositionGridMeters"));
        }

        [Test]
        public void Validate_VisibilityLingerTicksNegative_Throws()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.LingerTicks = -1;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.LingerTicks"));
        }

        [Test]
        public void Validate_VisibilityLingerTicksAboveRange_Throws()
        {
            // Mirrors VisibilityConfig's own [Range(0, 30)] ceiling.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.LingerTicks = 31;
            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Visibility.LingerTicks"));
        }

        // ------------------------------------------------------------------
        // Phase Ф5 fix-wave (I-1/I-2): the POSITIVE side of the same five
        // bands. Ten negatives above prove the builder rejects what is out of
        // range; not one of them proved it ACCEPTS what is in range, and the
        // gap was not academic — every one of these mutations survived the
        // whole 378-test run:
        //   * `minExclusive: true` added to HearPositionGridMeters, making
        //     `grid == 0` — a DOCUMENTED hard opt-out of quantization
        //     (VisibilitySystem.QuantizeAudiblePos) — unconfigurable;
        //   * the same for ExitHysteresis (hysteresis could no longer be
        //     switched off) and LingerTicks (nor the linger grace period);
        //   * ANY ceiling pulled inward — 150 -> 149, 200 -> 150, 20 -> 10,
        //     10 -> 5, 30 -> 10 — silently outlawing values the Inspector's
        //     own slider still offers;
        //   * the cross-check relaxed from `<` to `<=`, which would start
        //     rejecting the perfectly legal HearRadius == SightRadius.
        //
        // The invariant these pin, stated once: EVERY value VisibilityConfig's
        // own [Range] slider can produce must survive the builder. Expected
        // bounds are therefore read OFF THE ATTRIBUTES (VisibilityRange
        // below), never restated as literals — the builder's ceilings exist
        // precisely to mirror those attributes (SimConfigBuilder's own Р115
        // note), so a copied literal would stop mirroring them the moment the
        // slider moved and would go on passing against the stale number.
        // ------------------------------------------------------------------

        /// The [Range] band VisibilityConfig itself declares for `fieldName`,
        /// read from the attribute rather than restated. `UnityEngine.` is
        /// spelled out because NUnit.Framework has a RangeAttribute of its own
        /// and this file uses both namespaces.
        static (float Min, float Max) VisibilityRange(string fieldName)
        {
            System.Reflection.FieldInfo field = typeof(VisibilityConfig).GetField(fieldName);
            Assert.IsNotNull(field, $"test setup: VisibilityConfig has no public field {fieldName}");
            object[] attrs = field.GetCustomAttributes(typeof(UnityEngine.RangeAttribute), false);
            Assert.AreEqual(1, attrs.Length,
                $"test setup: {fieldName} must carry exactly one [Range] for this fixture to read its band from");
            var range = (UnityEngine.RangeAttribute)attrs[0];
            Assert.Less(range.min, range.max, $"test setup: {fieldName}'s [Range] must be a non-degenerate band");
            return (range.min, range.max);
        }

        [Test]
        public void Validate_VisibilitySightRadiusAtRangeFloor_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float floor = VisibilityRange(nameof(VisibilityConfig.SightRadius)).Min;
            vis.SightRadius = floor; // HearRadius keeps its default, comfortably above this
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(floor, cfg.Visibility.SightRadius, Eps);
        }

        [Test]
        public void Validate_VisibilitySightRadiusAtRangeCeiling_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float ceiling = VisibilityRange(nameof(VisibilityConfig.SightRadius)).Max;
            vis.SightRadius = ceiling;
            // HearRadius is raised to its OWN ceiling alongside, exactly as the
            // matching negative test does, so the cross-check stays quiet and
            // this fixture isolates the SightRadius ceiling.
            vis.HearRadius = VisibilityRange(nameof(VisibilityConfig.HearRadius)).Max;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(ceiling, cfg.Visibility.SightRadius, Eps);
        }

        [Test]
        public void Validate_VisibilityHearRadiusAtRangeFloor_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float floor = VisibilityRange(nameof(VisibilityConfig.HearRadius)).Min;
            vis.HearRadius = floor;
            // SightRadius must come down with it or the cross-check fires for
            // an unrelated reason. Its own [Range] floor is the lowest legal
            // place to put it, which is exactly where the cross-check needs it.
            vis.SightRadius = VisibilityRange(nameof(VisibilityConfig.SightRadius)).Min;
            Assert.LessOrEqual(vis.SightRadius, vis.HearRadius,
                "test setup: the two [Range] floors must leave the HearRadius >= SightRadius rule satisfiable");
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(floor, cfg.Visibility.HearRadius, Eps);
        }

        [Test]
        public void Validate_VisibilityHearRadiusAtRangeCeiling_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float ceiling = VisibilityRange(nameof(VisibilityConfig.HearRadius)).Max;
            vis.HearRadius = ceiling; // default SightRadius stays far below — cross-check quiet
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(ceiling, cfg.Visibility.HearRadius, Eps);
        }

        [Test]
        public void Validate_VisibilityExitHysteresisAtRangeFloor_DoesNotThrow()
        {
            // Zero is a legitimate setting, not an accident: it turns exit
            // hysteresis off, leaving VisibilitySystem's tracked-entity budget
            // at the plain SightRadius.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float floor = VisibilityRange(nameof(VisibilityConfig.ExitHysteresis)).Min;
            vis.ExitHysteresis = floor;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(floor, cfg.Visibility.ExitHysteresis, Eps);
        }

        [Test]
        public void Validate_VisibilityExitHysteresisAtRangeCeiling_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float ceiling = VisibilityRange(nameof(VisibilityConfig.ExitHysteresis)).Max;
            vis.ExitHysteresis = ceiling;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(ceiling, cfg.Visibility.ExitHysteresis, Eps);
        }

        [Test]
        public void Validate_VisibilityHearPositionGridMetersAtRangeFloor_DoesNotThrow()
        {
            // Zero is the DOCUMENTED hard opt-out of position quantization
            // (VisibilitySystem.QuantizeAudiblePos returns the exact position
            // for `grid <= 0`), so an exclusive floor here would make a
            // supported configuration impossible to express.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float floor = VisibilityRange(nameof(VisibilityConfig.HearPositionGridMeters)).Min;
            vis.HearPositionGridMeters = floor;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(floor, cfg.Visibility.HearPositionGridMeters, Eps);
        }

        [Test]
        public void Validate_VisibilityHearPositionGridMetersAtRangeCeiling_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            float ceiling = VisibilityRange(nameof(VisibilityConfig.HearPositionGridMeters)).Max;
            vis.HearPositionGridMeters = ceiling;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(ceiling, cfg.Visibility.HearPositionGridMeters, Eps);
        }

        [Test]
        public void Validate_VisibilityLingerTicksAtRangeFloor_DoesNotThrow()
        {
            // Zero disables the linger grace period outright — again a
            // setting, not an accident.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var floor = (int)VisibilityRange(nameof(VisibilityConfig.LingerTicks)).Min;
            vis.LingerTicks = floor;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(floor, cfg.Visibility.LingerTicks);
        }

        [Test]
        public void Validate_VisibilityLingerTicksAtRangeCeiling_DoesNotThrow()
        {
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var ceiling = (int)VisibilityRange(nameof(VisibilityConfig.LingerTicks)).Max;
            vis.LingerTicks = ceiling;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(ceiling, cfg.Visibility.LingerTicks);
        }

        [Test]
        public void Validate_VisibilityHearRadiusEqualsSightRadius_DoesNotThrow()
        {
            // The cross-check is `HearRadius < SightRadius` — EQUAL is legal
            // (hearing exactly as far as sight, no more). The only fixture
            // that ever set the two equal was
            // Validate_VisibilitySightRadiusAboveRange_Throws, which expects a
            // throw from the CEILING, so relaxing the cross-check to `<=`
            // broke nothing. Done at the DEFAULT SightRadius, deliberately
            // away from any [Range] end, so this test isolates the cross-check
            // and nothing else.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            vis.HearRadius = vis.SightRadius;
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(cfg.Visibility.SightRadius, cfg.Visibility.HearRadius, Eps,
                "HearRadius == SightRadius is a legal config and must reach SimConfig unchanged");
        }

        static void AssertHeroEqual(HeroSimConfig e, HeroSimConfig a)
        {
            Assert.AreEqual(e.MaxSpeed, a.MaxSpeed, Eps);
            Assert.AreEqual(e.Accel, a.Accel, Eps);
            Assert.AreEqual(e.Friction, a.Friction, Eps);
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.MaxHp, a.MaxHp, Eps);
            Assert.AreEqual(e.DashSpeed, a.DashSpeed, Eps);
            Assert.AreEqual(e.DashDuration, a.DashDuration, Eps);
            Assert.AreEqual(e.DashCooldown, a.DashCooldown, Eps);
            Assert.AreEqual(e.DashIframes, a.DashIframes, Eps);
            Assert.AreEqual(e.DashBufferWindow, a.DashBufferWindow, Eps);
            Assert.AreEqual(e.LegsTop, a.LegsTop, Eps);
            Assert.AreEqual(e.BodyTop, a.BodyTop, Eps);
            Assert.AreEqual(e.HeadTop, a.HeadTop, Eps);
            Assert.AreEqual(e.LegsDamageMult, a.LegsDamageMult, Eps);
            Assert.AreEqual(e.BodyDamageMult, a.BodyDamageMult, Eps);
            Assert.AreEqual(e.HeadDamageMult, a.HeadDamageMult, Eps);
            Assert.AreEqual(e.SlideProfileTop, a.SlideProfileTop, Eps);
            Assert.AreEqual(e.MuzzleHeight, a.MuzzleHeight, Eps);
            Assert.AreEqual(e.SlideMuzzleHeight, a.SlideMuzzleHeight, Eps);
            Assert.AreEqual(e.MaxAimHeight, a.MaxAimHeight, Eps);
            Assert.AreEqual(e.StaminaMax, a.StaminaMax, Eps);
            Assert.AreEqual(e.DashStaminaCost, a.DashStaminaCost, Eps);
            Assert.AreEqual(e.SlideStaminaCost, a.SlideStaminaCost, Eps);
            Assert.AreEqual(e.StaminaRegenPerSec, a.StaminaRegenPerSec, Eps);
            Assert.AreEqual(e.StaminaRegenDelay, a.StaminaRegenDelay, Eps);
            Assert.AreEqual(e.LinkRefund, a.LinkRefund, Eps);
            Assert.AreEqual(e.SlideSpeed, a.SlideSpeed, Eps);
            Assert.AreEqual(e.SlideDuration, a.SlideDuration, Eps);
            Assert.AreEqual(e.SlideSteerRadPerSec, a.SlideSteerRadPerSec, Eps);
            Assert.AreEqual(e.SlideMinSpeedFrac, a.SlideMinSpeedFrac, Eps);
            Assert.AreEqual(e.RunUpSeconds, a.RunUpSeconds, Eps);
            Assert.AreEqual(e.RunUpDecayMult, a.RunUpDecayMult, Eps);
            Assert.AreEqual(e.SlideBufferWindow, a.SlideBufferWindow, Eps);
            Assert.AreEqual(e.LinkWindowSeconds, a.LinkWindowSeconds, Eps);
            Assert.AreEqual(e.PostDashSlideWindow, a.PostDashSlideWindow, Eps);
            Assert.AreEqual(e.SlideWallStopDot, a.SlideWallStopDot, Eps);
            Assert.AreEqual(e.RicochetRetention, a.RicochetRetention, Eps);
            Assert.AreEqual(e.AimMoveSpeedFrac, a.AimMoveSpeedFrac, Eps);
            Assert.AreEqual(e.AimSlideSpeedMult, a.AimSlideSpeedMult, Eps);
            Assert.AreEqual(e.AimSettleSeconds, a.AimSettleSeconds, Eps);
            // Stage 2 Task 8: documented deviation from the task-8 brief's Files
            // list (ConfigTests.cs isn't listed there, same established
            // discrepancy category as Task 4's AssertArenaEqual precedent right
            // below in this file) — without this the new field silently drops
            // out of Build_DefaultAssets_MatchesTestConfigsBaseline's coverage.
            Assert.AreEqual(e.EdgeRequestMinTicks, a.EdgeRequestMinTicks);
            // Stage 3 Task 3: same documented deviation as EdgeRequestMinTicks
            // right above — PickupRadius genuinely flows SO -> builder ->
            // SimConfig, so it needs the same coverage. Coordinator
            // fix-round (Ф3 review A-9/m4): this doc used to contrast
            // PickupRadius against "CellsOnDeath/CorpseCellFraction, which
            // have no SO source yet, R-3" — Т13 shipped; both now live in
            // LootSimConfig and are covered by AssertLootEqual below, not
            // this parenthetical.
            Assert.AreEqual(e.PickupRadius, a.PickupRadius, Eps);
            // Stage 3 Task 4: same documented deviation as PickupRadius/
            // EdgeRequestMinTicks above — InventoryCapacity/MaxInventoryItems
            // genuinely flow SO -> builder -> SimConfig, so they need the
            // same coverage.
            Assert.AreEqual(e.InventoryCapacity, a.InventoryCapacity);
            Assert.AreEqual(e.MaxInventoryItems, a.MaxInventoryItems);
        }

        static void AssertWeaponEqual(WeaponSimConfig e, WeaponSimConfig a)
        {
            Assert.AreEqual(e.FireInterval, a.FireInterval, Eps);
            Assert.AreEqual(e.ProjectileSpeed, a.ProjectileSpeed, Eps);
            Assert.AreEqual(e.ProjectileRadius, a.ProjectileRadius, Eps);
            Assert.AreEqual(e.ProjectileLifetime, a.ProjectileLifetime, Eps);
            Assert.AreEqual(e.Damage, a.Damage, Eps);
            Assert.AreEqual(e.SpreadRad, a.SpreadRad, Eps);
            Assert.AreEqual(e.RecoilPerShotRad, a.RecoilPerShotRad, Eps);
            Assert.AreEqual(e.RecoilRecoveryRadPerSec, a.RecoilRecoveryRadPerSec, Eps);
            Assert.AreEqual(e.RecoilMaxRad, a.RecoilMaxRad, Eps);
            Assert.AreEqual(e.MuzzleOffset, a.MuzzleOffset, Eps);
            Assert.AreEqual(e.CanFireWhileDash, a.CanFireWhileDash);
            Assert.AreEqual(e.CanFireWhileSlide, a.CanFireWhileSlide);
            Assert.AreEqual(e.SpreadRunMult, a.SpreadRunMult, Eps);
            Assert.AreEqual(e.SpreadSlideMult, a.SpreadSlideMult, Eps);
            Assert.AreEqual(e.RunSpreadSpeedFrac, a.RunSpreadSpeedFrac, Eps);
            // Stage 3 Task 2: AmmoStart is EXCLUDED here on purpose — see
            // Build_DefaultAssets_MatchesTestConfigsBaseline's own comment,
            // same documented-deviation category as ArenaConfig.BarrierTop.
            Assert.AreEqual(e.ShotsPerCell, a.ShotsPerCell);
            Assert.AreEqual(e.AmmoMax, a.AmmoMax);
            Assert.AreEqual(e.EmergencyFireInterval, a.EmergencyFireInterval, Eps);
        }

        static void AssertMobEqual(MobSimConfig e, MobSimConfig a)
        {
            Assert.AreEqual(e.MaxSpeed, a.MaxSpeed, Eps);
            Assert.AreEqual(e.Accel, a.Accel, Eps);
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.MaxHp, a.MaxHp, Eps);
            Assert.AreEqual(e.ContactDamage, a.ContactDamage, Eps);
            Assert.AreEqual(e.AttackRange, a.AttackRange, Eps);
            Assert.AreEqual(e.TelegraphSeconds, a.TelegraphSeconds, Eps);
            Assert.AreEqual(e.AttackCooldown, a.AttackCooldown, Eps);
            Assert.AreEqual(e.PreferredRange, a.PreferredRange, Eps);
            Assert.AreEqual(e.RangeTolerance, a.RangeTolerance, Eps);
            Assert.AreEqual(e.StrafeSpeed, a.StrafeSpeed, Eps);
            Assert.AreEqual(e.FireInterval, a.FireInterval, Eps);
            Assert.AreEqual(e.ProjectileSpeed, a.ProjectileSpeed, Eps);
            Assert.AreEqual(e.ProjectileRadius, a.ProjectileRadius, Eps);
            Assert.AreEqual(e.ProjectileLifetime, a.ProjectileLifetime, Eps);
            Assert.AreEqual(e.ProjectileDamage, a.ProjectileDamage, Eps);
            Assert.AreEqual(e.LeadFactor, a.LeadFactor, Eps);
            Assert.AreEqual(e.SeparationRadius, a.SeparationRadius, Eps);
            Assert.AreEqual(e.SeparationStrength, a.SeparationStrength, Eps);
            Assert.AreEqual(e.AvoidLookahead, a.AvoidLookahead, Eps);
            Assert.AreEqual(e.AvoidMargin, a.AvoidMargin, Eps);
            Assert.AreEqual(e.LegsTop, a.LegsTop, Eps);
            Assert.AreEqual(e.BodyTop, a.BodyTop, Eps);
            Assert.AreEqual(e.HeadTop, a.HeadTop, Eps);
            Assert.AreEqual(e.LegsDamageMult, a.LegsDamageMult, Eps);
            Assert.AreEqual(e.BodyDamageMult, a.BodyDamageMult, Eps);
            Assert.AreEqual(e.HeadDamageMult, a.HeadDamageMult, Eps);
            Assert.AreEqual(e.MuzzleHeight, a.MuzzleHeight, Eps);
            Assert.AreEqual(e.SwingLeadFactor, a.SwingLeadFactor, Eps);
            Assert.AreEqual(e.SwingLeadMaxMeters, a.SwingLeadMaxMeters, Eps);
        }

        static void AssertWaveEqual(WaveSimConfig e, WaveSimConfig a)
        {
            Assert.AreEqual(e.FirstWaveDelay, a.FirstWaveDelay, Eps);
            Assert.AreEqual(e.SpawnRingInset, a.SpawnRingInset, Eps);
            Assert.AreEqual(e.MinSpawnDistanceToPlayer, a.MinSpawnDistanceToPlayer, Eps);
            // Task Т6 (app-ggvz, owner decision К5): BaseCount is EXCLUDED
            // here on purpose — see
            // Build_DefaultAssets_MatchesTestConfigsBaseline's own comment,
            // same documented-deviation category as ArenaConfig.BarrierTop/
            // WeaponConfig.AmmoStart above.
            Assert.AreEqual(e.CountGrowth, a.CountGrowth);
            Assert.AreEqual(e.MaxMobsPerWave, a.MaxMobsPerWave);
            Assert.AreEqual(e.MaxSpawnAttempts, a.MaxSpawnAttempts);
            Assert.AreEqual(e.FallbackSlots, a.FallbackSlots);
            Assert.AreEqual(e.GunnerShareBase, a.GunnerShareBase, Eps);
            Assert.AreEqual(e.GunnerShareGrowth, a.GunnerShareGrowth, Eps);
            // Stage 2 Task 16: same documented deviation from the brief's Files
            // list as AssertArenaEqual's own Task 4 note below — without this the
            // new field silently drops out of
            // Build_DefaultAssets_MatchesTestConfigsBaseline's coverage.
            Assert.AreEqual(e.PerPlayerCountFrac, a.PerPlayerCountFrac, Eps);
            // Ф2 review A-4: the four numbers Т11 added stopped here for a
            // whole phase. Nothing else covers them — WaveZoneTests build their
            // configs from TestConfigs and never touch the builder, and until
            // this round the builder validated none of them — so deleting a
            // copy line in Build left the whole suite green while the shipped
            // game lost the middle zone's elites. That is Р251 verbatim, and
            // AssertArenaEqual's own note warns about it three times over.
            // (ZoneWeights was the fourth of them and stood right here until
            // Т4 deleted the field with the shared wave budget it weighted.)
            Assert.AreEqual(e.EliteShareMiddle, a.EliteShareMiddle, Eps);
            // Task Т6 (app-ggvz, decision Р311): EliteShareOuterGrowth is
            // EXCLUDED here on purpose — see
            // Build_DefaultAssets_MatchesTestConfigsBaseline's own comment,
            // same documented-deviation category as ArenaConfig.BarrierTop/
            // WeaponConfig.AmmoStart above.
            Assert.AreEqual(e.EliteShareOuterCap, a.EliteShareOuterCap, Eps);
            // Task Т2 (app-ggvz, spec §3.8 Р337): MaxSpawnsPerZonePerTick
            // agrees on both sides of the fixture (2 = 2) — ordinary
            // equality, same as every field above. WavePauseByZone/
            // MaxAliveByZone/DifficultyStepSeconds are deliberately
            // EXCLUDED here — same documented-deviation category as
            // AmmoStart/BarrierTop — and get their own triple-form
            // assertions at Build_DefaultAssets_MatchesTestConfigsBaseline's
            // own call site instead, right after this method's own call.
            Assert.AreEqual(e.MaxSpawnsPerZonePerTick, a.MaxSpawnsPerZonePerTick);
        }

        /// Ф2 review C1: the five MatchFlowConfig numbers, pinned against the
        /// TestConfigs baseline the way every other section here is. Mirrors
        /// AssertVisibilityEqual's own role — without it the SO's C# defaults
        /// and the test baseline are joined by nothing at all.
        static void AssertFlowEqual(MatchFlowSimConfig e, MatchFlowSimConfig a)
        {
            Assert.AreEqual(e.GateDelaySeconds, a.GateDelaySeconds, Eps);
            Assert.AreEqual(e.ExtractChannelSeconds, a.ExtractChannelSeconds, Eps);
            Assert.AreEqual(e.RetinueCount, a.RetinueCount);
            Assert.AreEqual(e.RetinueRespawnSeconds, a.RetinueRespawnSeconds, Eps);
            Assert.AreEqual(e.DirectorReserveSlots, a.DirectorReserveSlots);
        }

        /// Coordinator fix-round (Ф3 review I1): the EIGHT LootSimConfig
        /// fields that genuinely mirror LootConfig.cs's own C# defaults —
        /// the other seven (DropChance/CrateCount/CacheCountMiddle/
        /// CacheCountCore/RepairKitChance/CellsPerMob/CorpseCellFraction)
        /// are deliberately golden-safety-zeroed in TestConfigs and get
        /// their own triple-form assertions at this method's own call site
        /// instead (same AmmoStart/BarrierTop shape). Field order mirrors
        /// LootSimConfig's own declared order, the same convention every
        /// other AssertXEqual in this file follows.
        static void AssertLootEqual(LootSimConfig e, LootSimConfig a)
        {
            Assert.AreEqual(e.RepairKitHealAmount, a.RepairKitHealAmount, Eps);
            Assert.AreEqual(e.RepairKitChannelSeconds, a.RepairKitChannelSeconds, Eps);
            Assert.AreEqual(e.TransferSeconds.Length, a.TransferSeconds.Length);
            for (int i = 0; i < e.TransferSeconds.Length; i++)
                Assert.AreEqual(e.TransferSeconds[i], a.TransferSeconds[i], Eps, $"TransferSeconds[{i}]");
            Assert.AreEqual(e.LootSpawnAttempts, a.LootSpawnAttempts);
            Assert.AreEqual(e.LootFallbackSlots, a.LootFallbackSlots);
            Assert.AreEqual(e.PickupTtlSeconds, a.PickupTtlSeconds, Eps);
            Assert.AreEqual(e.ContainerTtlSeconds, a.ContainerTtlSeconds, Eps);
            Assert.AreEqual(e.LootRadius, a.LootRadius, Eps);
        }

        /// Coordinator fix-round (Ф3 review I1/C1): the 25 catalog literals
        /// (five records x five fields) were duplicated ItemCatalog.cs <->
        /// TestConfigs.cs with no executable link at all — an owner adding
        /// a sixth record or moving a SlotCost would not redden a single
        /// test. Length first (index-out-of-range on a short array
        /// diagnoses nothing), then every field of every record.
        static void AssertItemsEqual(ItemDef[] e, ItemDef[] a)
        {
            Assert.AreEqual(e.Length, a.Length, "ItemCatalog.Items.Length must reach SimConfig.Items");
            for (int i = 0; i < e.Length; i++)
            {
                Assert.AreEqual(e[i].Id, a[i].Id, $"Items[{i}].Id");
                Assert.AreEqual(e[i].Tier, a[i].Tier, $"Items[{i}].Tier");
                Assert.AreEqual(e[i].SlotCost, a[i].SlotCost, $"Items[{i}].SlotCost");
                Assert.AreEqual(e[i].CreditValue, a[i].CreditValue, $"Items[{i}].CreditValue");
                Assert.AreEqual(e[i].Kind, a[i].Kind, $"Items[{i}].Kind");
            }
        }

        static void AssertArenaEqual(ArenaSimConfig e, ArenaSimConfig a)
        {
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.ObstacleCount, a.ObstacleCount);
            Assert.AreEqual(e.MaxMobs, a.MaxMobs);
            Assert.AreEqual(e.MaxProjectiles, a.MaxProjectiles);
            Assert.AreEqual(e.MaxEventsPerFrame, a.MaxEventsPerFrame);
            // Stage 2 Task 4: documented deviation from the task-4 brief's Files list —
            // ConfigTests.cs isn't listed there, but without this the two new
            // fields silently drop out of Build_DefaultAssets_MatchesTestConfigsBaseline's
            // coverage (see task-4-report.md).
            Assert.AreEqual(e.MaxPlayers, a.MaxPlayers);
            Assert.AreEqual(e.PlayerSpawnRingFrac, a.PlayerSpawnRingFrac, Eps);
            // Stage 3 Task 3: same documented deviation as MaxPlayers above —
            // MaxPickups genuinely flows SO -> builder -> SimConfig.
            Assert.AreEqual(e.MaxPickups, a.MaxPickups);
            // Stage 3 Task 8: same documented deviation as MaxPickups above —
            // every new Arena field genuinely flows SO -> builder -> SimConfig,
            // zone/door/portal arrays included (both sides are empty before
            // Т12, so this is an equal-empty-arrays comparison today, not a
            // vacuous one — TestConfigs.DefaultArena()'s own comment).
            Assert.AreEqual(e.DoorClearance, a.DoorClearance, Eps);
            Assert.AreEqual(e.ExtractRadius, a.ExtractRadius, Eps);
            Assert.AreEqual(e.MaxContainers, a.MaxContainers);
            Assert.AreEqual(e.MaxContainerSlots, a.MaxContainerSlots);
            Assert.AreEqual(e.ZoneRadius.Length, a.ZoneRadius.Length);
            for (int i = 0; i < e.ZoneRadius.Length; i++)
                Assert.AreEqual(e.ZoneRadius[i], a.ZoneRadius[i], Eps);
            Assert.AreEqual(e.ZoneWallCount, a.ZoneWallCount);
            for (int i = 0; i < e.ZoneWallCount; i++)
            {
                Assert.AreEqual(e.ZoneWallRadius[i], a.ZoneWallRadius[i], Eps);
                Assert.AreEqual(e.ZoneWallHalfWidth[i], a.ZoneWallHalfWidth[i], Eps);
                Assert.AreEqual(e.ZoneWallDoorStart[i], a.ZoneWallDoorStart[i]);
                Assert.AreEqual(e.ZoneWallDoorCount[i], a.ZoneWallDoorCount[i]);
            }
            Assert.AreEqual(e.DoorCenterRad.Length, a.DoorCenterRad.Length);
            for (int i = 0; i < e.DoorCenterRad.Length; i++)
            {
                Assert.AreEqual(e.DoorCenterRad[i], a.DoorCenterRad[i], Eps);
                Assert.AreEqual(e.DoorFreeWidth[i], a.DoorFreeWidth[i], Eps);
            }
            Assert.AreEqual(e.ExtractPos.Length, a.ExtractPos.Length);
            for (int i = 0; i < e.ExtractPos.Length; i++)
            {
                Assert.AreEqual(e.ExtractPos[i].x, a.ExtractPos[i].x, Eps);
                Assert.AreEqual(e.ExtractPos[i].y, a.ExtractPos[i].y, Eps);
                Assert.AreEqual(e.ExtractZone[i], a.ExtractZone[i]);
                Assert.AreEqual(e.ExtractKind[i], a.ExtractKind[i]);
            }
            for (int i = 0; i < e.ObstacleCount; i++)
            {
                Assert.AreEqual(e.ObstaclePos[i].x, a.ObstaclePos[i].x, Eps);
                Assert.AreEqual(e.ObstaclePos[i].y, a.ObstaclePos[i].y, Eps);
                Assert.AreEqual(e.ObstacleRadius[i], a.ObstacleRadius[i], Eps);
            }
            // Stage 2 Task 16 (carryover-t16 item 7a): the four wall fields land
            // in ArenaSimConfig in Task 11 but only get FILLED by the builder
            // here — until they are compared element-wise, exactly like the
            // circles above, they stay outside
            // Build_DefaultAssets_MatchesTestConfigsBaseline's coverage.
            Assert.AreEqual(e.WallCount, a.WallCount);
            for (int i = 0; i < e.WallCount; i++)
            {
                Assert.AreEqual(e.WallA[i].x, a.WallA[i].x, Eps);
                Assert.AreEqual(e.WallA[i].y, a.WallA[i].y, Eps);
                Assert.AreEqual(e.WallB[i].x, a.WallB[i].x, Eps);
                Assert.AreEqual(e.WallB[i].y, a.WallB[i].y, Eps);
                Assert.AreEqual(e.WallHalfWidth[i], a.WallHalfWidth[i], Eps);
            }
        }

        // --- Stage 3 Task 12: the three-zone arena in data (spec §3.2/§3.13/
        // §3.15, owner decisions R-72/R-77/R-79). Everything below reads the
        // C# DEFAULTS through the builder, never the shipped .asset (spec §0's
        // two-sources discipline, MakeDefaults' own contract).

        /// Every candidate player spawn point the world can ever use, with a
        /// tag naming it — every point of every ring size from 1 up to
        /// MaxPlayers. The arena center left this set in Stage 3 Ф5-0 (owner
        /// decision R-173) together with the solo special case that used to
        /// put a player there, and the n=1 point took its place — same
        /// change SimConfigBuilder's own three clearance loops made, mirrored
        /// here. Deliberately WIDER than SpawnPoints
        /// above (which walks one ring, the full lobby): rings of different
        /// sizes are not nested, and SimConfigBuilder.Validate's own
        /// obstacle/wall/arc clearance loops walk exactly this set — a rule
        /// stated against the full lobby alone would miss the two-player
        /// layout, which is how the 180 degree portal of spec §3.15 came to
        /// sit 1.96 m from a real spawn point (owner decision R-72).
        static (float2 Pos, string Tag)[] AllSpawnPoints(in ArenaSimConfig arena)
        {
            var pts = new System.Collections.Generic.List<(float2, string)>();
            for (int n = 1; n <= arena.MaxPlayers; n++)
                for (int s = 0; s < n; s++)
                    pts.Add((Geometry.SpawnPosFor(s, n, in arena), $"ring {n}/point {s}"));
            return pts.ToArray();
        }

        [Test]
        public void Build_MatchFlowConfig_ReachesSimConfig()
        {
            // Errata E-2: the struct and SimConfig.Flow landed in Т1, the SO
            // and this wiring in Т12. Without the wiring every number below
            // reaches the simulation as a zero — the gate would open the
            // instant the Director died and the extraction channel would
            // complete in a single tick, both silently.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var flow = ScriptableObject.CreateInstance<MatchFlowConfig>();

            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis, flow: flow);

            Assert.AreEqual(flow.GateDelaySeconds, cfg.Flow.GateDelaySeconds, Eps,
                "MatchFlowConfig.GateDelaySeconds must reach MatchFlowSimConfig");
            Assert.AreEqual(flow.ExtractChannelSeconds, cfg.Flow.ExtractChannelSeconds, Eps,
                "MatchFlowConfig.ExtractChannelSeconds must reach MatchFlowSimConfig");
            Assert.AreEqual(flow.RetinueCount, cfg.Flow.RetinueCount,
                "MatchFlowConfig.RetinueCount must reach MatchFlowSimConfig");
            Assert.AreEqual(flow.RetinueRespawnSeconds, cfg.Flow.RetinueRespawnSeconds, Eps,
                "MatchFlowConfig.RetinueRespawnSeconds must reach MatchFlowSimConfig");
            Assert.AreEqual(flow.DirectorReserveSlots, cfg.Flow.DirectorReserveSlots,
                "MatchFlowConfig.DirectorReserveSlots must reach MatchFlowSimConfig");
        }

        [Test]
        public void Build_CarriesEveryShippedSection_NoneLeftAtZero()
        {
            // Stage 3 Т22 (coordinator R-186) REPLACES the old
            // Build_MatchFlowConfigOmitted_LeavesFlowAtZero, which pinned the
            // very contract this task removed: "an omitted section arrives as
            // zeros". It cannot be restated as a test, because the omission no
            // longer compiles — the five parameters are required. What CAN be
            // stated, and matters more, is the property that made the old
            // contract dangerous in the first place: a configuration built the
            // way the game builds it carries all five sections, so no rule
            // downstream is quietly running against zeros (mutation M8, Т12).
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.Greater(cfg.Flow.GateDelaySeconds, 0f, "Flow reached SimConfig");
            Assert.Greater(cfg.Flow.DirectorReserveSlots, 0, "…including the slot reserve");
            Assert.Greater(cfg.Elite.MaxHp, 0f, "the Elite section reached SimConfig");
            Assert.Greater(cfg.Director.MaxHp, 0f, "…and the Director section");
            Assert.Greater(cfg.Items.Length, 0, "…and the item catalog");
            Assert.Greater(cfg.Loot.CellsPerMob.Length, 0, "…and the loot table");
        }

        [Test]
        public void Build_ItemCatalogAndLootConfig_ReachSimConfig()
        {
            // Stage 3 Task 13: same "reaches SimConfig" contract as
            // Build_MatchFlowConfig_ReachesSimConfig above, for the two
            // newest trailing parameters.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            var loot = ScriptableObject.CreateInstance<LootConfig>();

            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis,
                items: items, loot: loot);

            Assert.AreEqual(items.Items.Length, cfg.Items.Length,
                "ItemCatalog.Items must reach SimConfig.Items, record for record");
            for (int i = 0; i < items.Items.Length; i++)
            {
                Assert.AreEqual(items.Items[i].Id, cfg.Items[i].Id, $"Items[{i}].Id");
                Assert.AreEqual(items.Items[i].Tier, cfg.Items[i].Tier, $"Items[{i}].Tier");
                Assert.AreEqual(items.Items[i].SlotCost, cfg.Items[i].SlotCost, $"Items[{i}].SlotCost");
                Assert.AreEqual(items.Items[i].CreditValue, cfg.Items[i].CreditValue, $"Items[{i}].CreditValue");
                Assert.AreEqual(items.Items[i].Kind, cfg.Items[i].Kind, $"Items[{i}].Kind");
            }
            Assert.AreEqual(loot.RepairKitHealAmount, cfg.Loot.RepairKitHealAmount, Eps,
                "LootConfig.RepairKitHealAmount must reach LootSimConfig");
            Assert.AreEqual(loot.LootRadius, cfg.Loot.LootRadius, Eps,
                "LootConfig.LootRadius must reach LootSimConfig");
            Assert.AreEqual(loot.CrateCount, cfg.Loot.CrateCount,
                "LootConfig.CrateCount must reach LootSimConfig");
        }

        [Test]
        public void Build_ItemCatalogAndLoot_AreOverridableWithoutLosingTheRest()
        {
            // Stage 3 Т22 (coordinator R-186) REPLACES
            // Build_ItemCatalogAndLootOmitted_LeaveEmptyAndZero, the sibling of
            // the flow test above: "omitted leaves it empty" was the contract
            // this task removed, and an empty catalog was precisely what let
            // the MinCatalogSlotCost rule read as satisfied while resting on
            // nothing (R-84). What replaces it is the property tests actually
            // need from the shipped home: ONE section can be varied to prove
            // ONE rule, and the other four stay real while you do it.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            items.Items = new[] { new ItemDef { Id = 9, Tier = 1, SlotCost = 2, CreditValue = 5 } };

            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis, items: items);

            Assert.AreEqual(1, cfg.Items.Length, "the override reached SimConfig");
            Assert.AreEqual(9, cfg.Items[0].Id, "…and it is the caller's own catalog");
            Assert.Greater(cfg.Loot.CrateCount, 0,
                "…while every section the caller did NOT override stays the shipped one — " +
                "which is the whole point of the home (a rule may not be weakened by absence)");
            Assert.Greater(cfg.Director.MaxHp, 0f, "…including the Director section");
        }

        [Test]
        public void Validate_MaxContainerSlotsRuleUsesRealCatalogMinimum()
        {
            // Stage 3 Task 13 (owner decision R-5/R-84, mutation witness for
            // "min(SlotCost) comes from the real catalog"): a fixture the
            // OLD hardcoded-1 assumption and the REAL per-item minimum
            // disagree on. Hero.InventoryCapacity is MakeDefaults' own
            // default (8); Arena.MaxContainerSlots is forced to 4, BELOW the
            // shipped 8. At the real catalog's min(SlotCost) = 2 the rule's
            // own floor is 8/2 = 4, so 4 >= 4 passes; under the assumption
            // of 1 (an empty/omitted catalog) the floor would be 8/1 = 8,
            // and 4 < 8 would wrongly throw.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            a.MaxContainerSlots = 4;
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            items.Items = new[]
            {
                new ItemDef { Id = 1, Tier = 1, SlotCost = 2, CreditValue = 15, Kind = ItemKind.Trophy },
            };

            Assert.DoesNotThrow(() => BuildShipped(h, w, c, g, wv, a, vis, items: items),
                "MaxContainerSlots=4 must satisfy InventoryCapacity(8)/min(SlotCost)(2)=4 " +
                "under the REAL catalog minimum, not the empty-catalog assumption's 8");
        }

        [Test]
        public void Validate_RejectsCellsPerMobWrongLength()
        {
            // Coordinator R-96: LootDrops.MobDeathCells indexes
            // Loot.CellsPerMob by MobType (four archetypes) with no bounds
            // guard of its own — a SUPPLIED LootConfig whose array was
            // shortened (an Inspector edit, not the omitted-`loot` case
            // Build's own branch already protects) must be refused here,
            // named, rather than crashing IndexOutOfRangeException on the
            // first mob death.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var loot = ScriptableObject.CreateInstance<LootConfig>();
            loot.CellsPerMob = new[] { 1, 1, 4 }; // three, not four — missing Director's own slot

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, loot: loot));
            StringAssert.Contains("CellsPerMob must have exactly 4 elements", ex.Message);
        }

        [Test]
        public void Validate_RejectsTransferSecondsWrongLength()
        {
            // Stage 3 Task 17: TransferSeconds finally has a live reader —
            // Loot.LootOps.Begin indexes it by the target item's own tier
            // (tier - 1, tiers 1..4) with no bounds guard of its own. R-92
            // says a rule is earned by the reader it protects, so the rule
            // arrives with the reader, exactly as CellsPerMob's above did in
            // Т13 and DropChance's did in Т16. A SUPPLIED LootConfig with a
            // shortened array (an Inspector edit — Build's own omitted-`loot`
            // branch already seeds a correct float[4]) must be refused here,
            // named, instead of crashing IndexOutOfRangeException on the first
            // container the player opens.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var loot = ScriptableObject.CreateInstance<LootConfig>();
            loot.TransferSeconds = new[] { 0.3f, 0.6f, 0.9f }; // three, not four — tier 4 has no time

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, loot: loot));
            StringAssert.Contains("TransferSeconds must have exactly 4 elements", ex.Message);
        }

        [Test]
        public void Validate_RejectsDirectorSectionWithNonPositiveNumbers()
        {
            // Stage 3 Т22 (coordinator R-186): the Elite/Director sections were
            // outside ValidateMob's sweep for two whole phases, because ninety
            // call sites could legally omit them. Now that they are required,
            // they are checked like every other archetype — and this is the
            // witness of that, without which the two new ValidateMob lines
            // could be deleted and the suite would not notice.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var director = ScriptableObject.CreateInstance<MobConfig>();
            director.MaxHp = 0f;

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, director: director));
            StringAssert.Contains("Director.MaxHp must be > 0", ex.Message);
        }

        [Test]
        public void Validate_RejectsEliteHitZoneBandsOutOfOrder()
        {
            // The ValidateZones half of the same debt: an elite whose leg band
            // reaches above its body band is a hit-zone table no shot can be
            // resolved against, and it used to reach the simulation unchecked.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var elite = ScriptableObject.CreateInstance<MobConfig>();
            elite.LegsTop = elite.BodyTop + 1f;

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, elite: elite));
            StringAssert.Contains("Elite.LegsTop must be < Elite.BodyTop", ex.Message);
        }

        [Test]
        public void Validate_RejectsReserveSmallerThanDirectorPlusRetinue()
        {
            // Stage 3 Т22 (coordinator R-181): the reserve is what makes the
            // retinue top-up need no stored debt at all — with
            // DirectorReserveSlots = 1 + RetinueCount the wave ceiling
            // (MaxMobs - reserve), the Director and a full retinue add up to
            // exactly MaxMobs, so a fallen retinue slot is always free again
            // and the wave may never take it. A reserve below that quietly
            // reopens the very branch the arithmetic closes: a top-up that
            // hits the cap and, having nowhere to record itself, is simply
            // lost until the next period.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var flow = ScriptableObject.CreateInstance<MatchFlowConfig>();
            flow.RetinueCount = 2;
            flow.DirectorReserveSlots = 2; // one short: the Director himself has no slot

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, flow: flow));
            StringAssert.Contains("Flow.DirectorReserveSlots must be >= 1 + Flow.RetinueCount",
                ex.Message);
        }

        [Test]
        public void Validate_RejectsNonPositiveRetinueRespawn()
        {
            // The top-up period is a DIVISOR turned into whole ticks
            // (MatchFlowSystem, R-180): zero or negative seconds is not a
            // "fast retinue", it is a division whose answer the phase machine
            // would then take a modulo against.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var flow = ScriptableObject.CreateInstance<MatchFlowConfig>();
            flow.RetinueRespawnSeconds = 0f;

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis, flow: flow));
            StringAssert.Contains("Flow.RetinueRespawnSeconds must be > 0", ex.Message);
        }

        [Test]
        public void Validate_RejectsReserveThatLeavesNoRoomForWaves()
        {
            // The other end of the same rule: a reserve at or above MaxMobs
            // stops the wave director outright (its ceiling would be zero or
            // negative), which is a configuration no raid can be played on.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var arena = ScriptableObject.CreateInstance<ArenaConfig>();
            var flow = ScriptableObject.CreateInstance<MatchFlowConfig>();
            flow.DirectorReserveSlots = arena.MaxMobs;

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, arena, vis, flow: flow));
            StringAssert.Contains("Flow.DirectorReserveSlots must be < Arena.MaxMobs", ex.Message);
        }

        [Test]
        public void Build_EliteAndDirectorAssets_ReachSimConfig()
        {
            // Errata E-6 I5: MobEliteConfig/MobDirectorConfig are new ASSETS
            // of the existing MobConfig class, so their numbers cannot come
            // from that class's C# initializers (those are the chaser's) —
            // exactly the gunner's own situation, and handled the same way
            // (StageOneSceneBootstrap seeds the asset, the shared TestConfigs
            // baseline carries the test numbers). Director is the SUBJECT
            // here (ledger 227: the second element of the pair); Elite is
            // asserted alongside so a slot swap cannot pass.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            var expected = TestConfigs.Default();
            var elite = ScriptableObject.CreateInstance<MobConfig>();
            var director = ScriptableObject.CreateInstance<MobConfig>();
            elite.MaxHp = expected.Elite.MaxHp;
            elite.Radius = expected.Elite.Radius;
            director.MaxHp = expected.Director.MaxHp;
            director.Radius = expected.Director.Radius;

            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis, elite, director);

            Assert.AreEqual(expected.Director.MaxHp, cfg.Director.MaxHp, Eps,
                "MobDirectorConfig.MaxHp must reach SimConfig.Director");
            Assert.AreEqual(expected.Director.Radius, cfg.Director.Radius, Eps,
                "MobDirectorConfig.Radius must reach SimConfig.Director");
            Assert.AreEqual(expected.Elite.MaxHp, cfg.Elite.MaxHp, Eps,
                "MobEliteConfig.MaxHp must reach SimConfig.Elite");
            Assert.AreEqual(expected.Elite.Radius, cfg.Elite.Radius, Eps,
                "MobEliteConfig.Radius must reach SimConfig.Elite");
            Assert.AreNotEqual(cfg.Gunner.MaxHp, cfg.Elite.MaxHp,
                "an Elite that reads the Gunner's numbers is the exact defect this pins");
            Assert.AreNotEqual(cfg.Elite.MaxHp, cfg.Director.MaxHp,
                "and the two new archetypes must not collapse onto each other either");
        }

        [Test]
        public void BootstrapArchetypeSeeds_MatchTheTestConfigsBaseline()
        {
            // Ф2 fix-round (review B-I1). The Elite and Director profiles exist
            // TWICE: as ~30 literals each in StageOneSceneBootstrap's seeding
            // methods, which is what reaches the shipped .asset, and again in
            // TestConfigs.Default(), which is what every test runs on. Т12's
            // SeedMob closed the drift BETWEEN the three test fixtures; it did
            // nothing about the drift between the bootstrap and the baseline,
            // and that one was held together by a one-off grep. This test is
            // the binding.
            //
            // It deliberately does NOT merge the two sources: spec §0 keeps
            // them apart so a divergence can be DELIBERATE (Weapon.AmmoStart is
            // 120 in the asset and 400 in the baseline; Arena.BarrierTop is 3
            // and 0), and each such case is pinned by its own named assertion
            // in Build_DefaultAssets_MatchesTestConfigsBaseline. What this
            // catches is the UNdocumented kind. If a deliberate divergence is
            // ever wanted for these two archetypes, it belongs here as a named
            // exception with its reason, exactly as those two have.
            var elite = ScriptableObject.CreateInstance<MobConfig>();
            var director = ScriptableObject.CreateInstance<MobConfig>();
            Ring.Editor.StageOneSceneBootstrap.ApplyEliteDefaults(elite);
            Ring.Editor.StageOneSceneBootstrap.ApplyDirectorDefaults(director);

            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis, elite, director);
            SimConfig expected = TestConfigs.Default();

            AssertMobEqual(expected.Elite, cfg.Elite);
            AssertMobEqual(expected.Director, cfg.Director);
            // And the two profiles must stay distinguishable: a copier that
            // seeded both from the same source would satisfy everything above.
            Assert.AreNotEqual(cfg.Elite.MaxHp, cfg.Director.MaxHp);
            Assert.AreNotEqual(cfg.Elite.Radius, cfg.Director.Radius);
        }

        [Test]
        public void Validate_ExtractZoneDisagreesWithPosition_Throws()
        {
            // Owner decision R-79: ExtractZone[i] and ExtractPos[i] describe
            // the same fact twice (which zone a portal stands in), and
            // Geometry.ZoneOf is the only arbiter. Т21/Т23 gate portal
            // availability off the declared byte, so a silent disagreement
            // would open a "middle" portal standing in the outer band —
            // a data defect no other rule in this file can see.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            Assert.GreaterOrEqual(a.ExtractZone.Length, 1,
                "the shipped layout must carry at least one portal for this rule to bite");
            byte honest = a.ExtractZone[0];
            a.ExtractZone[0] = (byte)(honest == (byte)Zone.Core ? Zone.Outer : Zone.Core);

            var ex = Assert.Throws<System.ArgumentException>(
                () => BuildShipped(h, w, c, g, wv, a, vis));
            StringAssert.Contains("ExtractZone", ex.Message);
        }

        [Test]
        public void Layout_PortalsKeepClearOfEverySpawnPoint()
        {
            // Spec §3.15 in its own words: "портал под ногами стартующего
            // обесценил бы решение уйти или остаться с первой же секунды
            // захода". Owner decision R-72: the rule is checked against EVERY
            // ring size, not just the full lobby — the spec's own 180 degree
            // portal cleared the three-player ring and landed 1.96 m from the
            // two-player one. The GATE is exempt by construction: spec §3.15
            // puts it at the core center, which IS the solo spawn point, and
            // it stays shut until the Director dies.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.GreaterOrEqual(cfg.Arena.ExtractPos.Length, 2,
                "the shipped layout must carry portals besides the gate");

            float needed = cfg.Arena.ExtractRadius + cfg.Hero.Radius;
            var spawns = AllSpawnPoints(in cfg.Arena);
            for (int p = 0; p < cfg.Arena.ExtractPos.Length; p++)
            {
                if (cfg.Arena.ExtractKind[p] == (byte)ExitKind.Gate) continue;
                foreach (var (pos, tag) in spawns)
                {
                    float d = math.distance(cfg.Arena.ExtractPos[p], pos);
                    Assert.GreaterOrEqual(d, needed,
                        $"portal [{p}] stands {d:F3} m from spawn point {tag}, " +
                        $"needs ExtractRadius+Hero.Radius={needed:F3}");
                }
            }
        }

        [Test]
        public void Layout_NoDirectRayFromAnyOuterDoorToCore()
        {
            // Spec §3.15: "из внешней двери не видно внутреннюю, прямого
            // коридора «спавн → ядро» нет, и это проверяется тестом (луч из
            // любой внешней двери в центр пересекает тело внутреннего
            // кольца)". The control arm below proves the block belongs to the
            // INNER ring and not to whatever else stands in the middle zone:
            // the same ray, stopped just outside that ring, is clear.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(2, cfg.Arena.ZoneWallCount,
                "the shipped layout must carry both zone walls");

            int outerWall = cfg.Arena.ZoneWallCount - 1;
            float ringR = cfg.Arena.ZoneWallRadius[outerWall];
            float controlR = cfg.Arena.ZoneWallRadius[0] + cfg.Arena.ZoneWallHalfWidth[0] + 2f;
            int start = cfg.Arena.ZoneWallDoorStart[outerWall];
            int count = cfg.Arena.ZoneWallDoorCount[outerWall];
            int innerStart = cfg.Arena.ZoneWallDoorStart[0];
            int innerCount = cfg.Arena.ZoneWallDoorCount[0];
            Assert.GreaterOrEqual(count, 1, "the outer zone wall must carry doors");

            for (int d = 0; d < count; d++)
            {
                float ang = cfg.Arena.DoorCenterRad[start + d];
                float2 dir = new float2(math.cos(ang), math.sin(ang));
                float2 doorPos = dir * ringR;

                // THE SPEC'S OWN SENTENCE, and deliberately not a weaker
                // paraphrase of it: §3.15 says "луч из любой внешней двери в
                // центр пересекает ТЕЛО ВНУТРЕННЕГО КОЛЬЦА". The first draft of
                // this test asserted only that the ray fails to REACH the core,
                // which is a different claim and a much cheaper one — mutation
                // M5 (aligning the two rings' doors, i.e. deleting the very
                // offset this test exists to pin) showed it survives: at
                // 30 deg the ray is stopped by obstacle circle 0, at 150 deg by
                // interior wall 0 (by 0.01 m!), at 270 deg by circle 2. Three
                // coincidences of the Stage 1/2 core layout, none of them the
                // door offset, and all three free to move at the owner's next
                // tuning pass. Asking Geometry.SegmentArc about the INNER RING
                // ALONE removes every coincidence from the answer.
                var innerDoorCenter =
                    new System.ReadOnlySpan<float>(cfg.Arena.DoorCenterRad, innerStart, innerCount);
                var innerDoorFreeWidth =
                    new System.ReadOnlySpan<float>(cfg.Arena.DoorFreeWidth, innerStart, innerCount);
                Assert.IsTrue(
                    Geometry.SegmentArc(doorPos, float2.zero, 0f, cfg.Arena.ZoneWallRadius[0],
                        cfg.Arena.ZoneWallHalfWidth[0], innerDoorCenter, innerDoorFreeWidth,
                        out _, out _),
                    $"the ray from outer door [{start + d}] to the core center must cross the INNER "
                    + "ring's own body — the two rings' doors must not line up into a straight "
                    + "spawn-to-core corridor (spec §3.15)");

                // The weaker companion, kept because it is also true and is
                // what a player actually experiences: nothing at all lets a
                // shot through from that door to the center.
                Assert.IsFalse(
                    Ring.Simulation.AI.Targeting.HasLineOfFire(doorPos, float2.zero, 0f, in cfg.Arena),
                    $"outer door [{start + d}] sees the core center — the doors of the two " +
                    "rings must not line up into a straight spawn-to-core corridor");
                Assert.IsTrue(
                    Ring.Simulation.AI.Targeting.HasLineOfFire(doorPos, dir * controlR, 0f, in cfg.Arena),
                    $"outer door [{start + d}] cannot even see the outside of the inner ring — " +
                    "the block above would then belong to the middle zone, not to the inner ring");
            }
        }

        [Test]
        public void Layout_EveryZoneWaveSpawnRingHasAFreeSlot()
        {
            // Spec §3.15's third layout requirement, in its own words:
            // "кольцо спавна ни одной зоны не заперто полностью". The two
            // rules SimConfigBuilder already carries cover only the ARC bodies
            // (R-55) and the arena-wide fallback ring — neither one looks at
            // what the middle and outer zones' own circles and walls do to
            // the per-zone rings this task ships them onto.
            var (h, w, c, g, wv, a, vis) = MakeDefaults();
            SimConfig cfg = BuildShipped(h, w, c, g, wv, a, vis);
            Assert.AreEqual(2, cfg.Arena.ZoneRadius.Length, "the shipped layout must carry zones");

            // The biggest body a WAVE can seat — the same three archetypes
            // SimConfigBuilder.MaxBodyRadius(waveArchetypesOnly: true) takes,
            // Elite included. Leaving the Elite out here would have understated
            // the body by 0.3 m and made this scan agree with the weakened rule
            // instead of the real one (coordinator finding on mutation M8).
            float bodyRadius = math.max(cfg.Chaser.Radius,
                math.max(cfg.Gunner.Radius, cfg.Elite.Radius));
            foreach (Zone zone in new[] { Zone.Outer, Zone.Middle, Zone.Core })
            {
                float ringRadius = Geometry.ZoneSpawnRingRadius(zone, in cfg.Arena,
                    cfg.Wave.SpawnRingInset);
                int free = FreeRingSlots(in cfg, bodyRadius, ringRadius);
                Assert.Greater(free, 0,
                    $"the {zone} zone's wave spawn ring (radius {ringRadius:F3}) is locked: " +
                    $"0 of {cfg.Wave.FallbackSlots} fallback slots can hold a mob of radius " +
                    $"{bodyRadius:F3} — no wave could ever spawn in that zone");
            }
        }

        /// Stage 2 Task 22 (carryover-t22 §2): mirrors AssertArenaEqual's own
        /// role for the newest SimConfig section — without this method,
        /// Build_DefaultAssets_MatchesTestConfigsBaseline cannot compare
        /// VisibilityConfig's five C# defaults against the TestConfigs
        /// baseline at all. Phase Ф5 fix-wave (I-4): C# defaults, NOT the
        /// shipped .asset — nothing in this file ever loads an asset from
        /// disk (see MakeDefaults).
        static void AssertVisibilityEqual(VisibilitySimConfig e, VisibilitySimConfig a)
        {
            Assert.AreEqual(e.SightRadius, a.SightRadius, Eps);
            Assert.AreEqual(e.HearRadius, a.HearRadius, Eps);
            Assert.AreEqual(e.ExitHysteresis, a.ExitHysteresis, Eps);
            Assert.AreEqual(e.LingerTicks, a.LingerTicks);
            Assert.AreEqual(e.HearPositionGridMeters, a.HearPositionGridMeters, Eps);
            // Stage 3 Task 13 (coordinator fix-round Ф3 review I1): the two
            // radii VisibilitySystem.Compute needs for a pickup/container
            // target — silently dropped out of this method's own coverage
            // since the task that added them, same class of gap as the rest
            // of this fix-round's I1 work.
            Assert.AreEqual(e.PickupRadiusForVisibility, a.PickupRadiusForVisibility, Eps);
            Assert.AreEqual(e.ContainerRadiusForVisibility, a.ContainerRadiusForVisibility, Eps);
        }
    }
}
