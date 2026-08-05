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

        static (HeroConfig, WeaponConfig, MobConfig, MobConfig, WaveConfig, ArenaConfig) MakeDefaults()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            var weapon = ScriptableObject.CreateInstance<WeaponConfig>();
            var chaser = ScriptableObject.CreateInstance<MobConfig>();
            var gunner = ScriptableObject.CreateInstance<MobConfig>();
            var wave = ScriptableObject.CreateInstance<WaveConfig>();
            var arena = ScriptableObject.CreateInstance<ArenaConfig>();
            return (hero, weapon, chaser, gunner, wave, arena);
        }

        /// Builds a SimConfig from a caller-supplied hero and default everything else —
        /// for zone-validation tests (Task 1) that only need to vary Hero fields.
        static SimConfig BuildWith(HeroConfig hero)
        {
            var (_, w, c, g, wv, a) = MakeDefaults();
            return SimConfigBuilder.Build(hero, w, c, g, wv, a);
        }

        [Test]
        public void Build_DefaultAssets_ProducesValidConfig()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);
            Assert.Greater(cfg.Hero.MaxSpeed, 0f);
            Assert.AreEqual(a.Obstacles.Length, cfg.Arena.ObstacleCount);
        }

        [Test]
        public void Build_ObstacleOutsideArena_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = new Vector2(100f, 0f), Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_NegativeSpeed_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            h.MaxSpeed = -1f;
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_ObstacleOverSpawnPoint_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = Vector2.zero, Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        // P-4.5: the SO defaults are the single source of truth for starting balance —
        // they must reproduce TestConfigs.Default() exactly (once the gunner asset's
        // fields are set to the real gunner numbers, since Task 6 only ships the
        // Chaser-shaped MobConfig default; the actual Gunner .asset arrives in Task 7).
        [Test]
        public void Build_DefaultAssets_MatchesTestConfigsBaseline()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            var expected = TestConfigs.Default();

            g.MaxSpeed = expected.Gunner.MaxSpeed;
            g.Accel = expected.Gunner.Accel;
            g.Radius = expected.Gunner.Radius;
            g.MaxHp = expected.Gunner.MaxHp;
            g.ContactDamage = expected.Gunner.ContactDamage;
            g.AttackRange = expected.Gunner.AttackRange;
            g.TelegraphSeconds = expected.Gunner.TelegraphSeconds;
            g.AttackCooldown = expected.Gunner.AttackCooldown;
            g.PreferredRange = expected.Gunner.PreferredRange;
            g.RangeTolerance = expected.Gunner.RangeTolerance;
            g.StrafeSpeed = expected.Gunner.StrafeSpeed;
            g.FireInterval = expected.Gunner.FireInterval;
            g.ProjectileSpeed = expected.Gunner.ProjectileSpeed;
            g.ProjectileRadius = expected.Gunner.ProjectileRadius;
            g.ProjectileLifetime = expected.Gunner.ProjectileLifetime;
            g.ProjectileDamage = expected.Gunner.ProjectileDamage;
            g.LeadFactor = expected.Gunner.LeadFactor;
            g.SeparationRadius = expected.Gunner.SeparationRadius;
            g.SeparationStrength = expected.Gunner.SeparationStrength;
            g.AvoidLookahead = expected.Gunner.AvoidLookahead;
            g.AvoidMargin = expected.Gunner.AvoidMargin;
            g.LegsTop = expected.Gunner.LegsTop;
            g.BodyTop = expected.Gunner.BodyTop;
            g.HeadTop = expected.Gunner.HeadTop;
            g.LegsDamageMult = expected.Gunner.LegsDamageMult;
            g.BodyDamageMult = expected.Gunner.BodyDamageMult;
            g.HeadDamageMult = expected.Gunner.HeadDamageMult;
            g.MuzzleHeight = expected.Gunner.MuzzleHeight;

            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);

            AssertHeroEqual(expected.Hero, cfg.Hero);
            AssertWeaponEqual(expected.Weapon, cfg.Weapon);
            AssertMobEqual(expected.Chaser, cfg.Chaser);
            AssertMobEqual(expected.Gunner, cfg.Gunner);
            AssertWaveEqual(expected.Wave, cfg.Wave);
            AssertArenaEqual(expected.Arena, cfg.Arena);

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
            var (h, w, c, g, wv, a) = MakeDefaults();
            c.SwingLeadFactor = 2.5f; // outside [0, 2]
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("SwingLeadFactor"));
        }

        [Test]
        public void Validate_SwingLeadMaxMetersNegative_Throws()
        {
            // I3 (final review wave, app-n6g): SwingLeadMaxMeters must stay
            // non-negative per archetype — ValidateMob never checked it.
            var (h, w, c, g, wv, a) = MakeDefaults();
            c.SwingLeadMaxMeters = -1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
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
        static int FreeRingSlots(in SimConfig cfg, float bodyRadius)
        {
            float ringRadius = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            int free = 0;
            for (int i = 0; i < cfg.Wave.FallbackSlots; i++)
            {
                float angle = 2f * math.PI * i / cfg.Wave.FallbackSlots;
                float2 p = ringRadius * new float2(math.cos(angle), math.sin(angle));
                bool blocked = false;
                for (int o = 0; o < cfg.Arena.ObstacleCount && !blocked; o++)
                    blocked = Geometry.CircleOverlap(p, bodyRadius,
                        cfg.Arena.ObstaclePos[o], cfg.Arena.ObstacleRadius[o]);
                for (int wIdx = 0; wIdx < cfg.Arena.WallCount && !blocked; wIdx++)
                    blocked = Geometry.OverlapsStadium(p, bodyRadius,
                        cfg.Arena.WallA[wIdx], cfg.Arena.WallB[wIdx], cfg.Arena.WallHalfWidth[wIdx]);
                if (!blocked) free++;
            }
            return free;
        }

        [Test]
        public void Layout_SomeSpawnPairHasNoLineOfSight()
        {
            // Spec §3.4 requirement (a): at least one pair of spawn points must
            // be unable to see each other from the very first tick, so a
            // three-way match does not open with everyone in everyone's sights.
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);
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
        public void Layout_P0P1PairBlockedSpecificallyByWalls()
        {
            // Fixwave Ф3 item 4: Layout_SomeSpawnPairHasNoLineOfSight above
            // passes even at Walls = {} — the circle obstacle at (-6,-30)
            // r3.2 happens to block the P0-P2 pair on its own (perpendicular
            // distance 3.019 m against its own 3.2 m radius, a scant 0.18 m
            // margin), so spec §3.4 (a) was satisfied by pure coincidence,
            // with no evidence the walls (spec §3.15's own comment: "one lone
            // wall breaking line of sight between the P0 and P1 spawn
            // points") contribute anything at all. This test pins the SPECIFIC
            // pair the spec's wall comment names (P0-P1) and proves a WALL is
            // what blocks it: blocked with the shipped walls in place, then
            // visible again on the identical layout with Walls = {} (circles
            // unchanged) — a circle alone cannot be the cause of THIS pair's
            // block if removing every wall restores it.
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfgWithWalls = SimConfigBuilder.Build(h, w, c, g, wv, a);
            float2[] ptsWithWalls = SpawnPoints(in cfgWithWalls.Arena);
            Assert.IsFalse(
                Ring.Simulation.AI.Targeting.HasLineOfFire(ptsWithWalls[0], ptsWithWalls[1], 0f, in cfgWithWalls.Arena),
                "P0-P1 must be blocked with the shipped walls in place");

            a.Walls = System.Array.Empty<ArenaConfig.Wall>();
            SimConfig cfgNoWalls = SimConfigBuilder.Build(h, w, c, g, wv, a);
            float2[] ptsNoWalls = SpawnPoints(in cfgNoWalls.Arena);
            Assert.IsTrue(
                Ring.Simulation.AI.Targeting.HasLineOfFire(ptsNoWalls[0], ptsNoWalls[1], 0f, in cfgNoWalls.Arena),
                "removing every wall must restore P0-P1 visibility — the block must come from " +
                "a wall, not a circle obstacle");
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

            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);

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
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);
            Assert.Greater(cfg.Arena.WallCount, 0,
                "the shipped layout must carry interior walls for this requirement to mean anything");

            float bodyRadius = math.max(cfg.Chaser.Radius, cfg.Gunner.Radius);
            Assert.Greater(FreeRingSlots(in cfg, bodyRadius), 0,
                "the wave spawn ring is fully blocked — no wave could ever spawn");
        }

        [Test]
        public void Validate_WallZeroHalfWidth_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(-5f, 0f), B = new Vector2(5f, 0f), HalfWidth = 0f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("HalfWidth"));
        }

        [Test]
        public void Validate_WallZeroLength_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(5f, 5f), B = new Vector2(5f, 5f), HalfWidth = 0.8f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
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
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(0f, 5f), B = new Vector2(2e-7f, 5f), HalfWidth = 0.8f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
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
            var (h, w, c, g, wv, a) = MakeDefaults();
            float halfWidth = 0.8f;
            float rimEnd = a.Radius - halfWidth; // passes "inside Radius - HalfWidth", fails 7c
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(rimEnd - 10f, 0f), B = new Vector2(rimEnd, 0f), HalfWidth = halfWidth } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
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
            var (h, w, c, g, wv, a) = MakeDefaults();
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
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("arena rim"));
        }

        [Test]
        public void Validate_WallOverPlayerSpawn_Throws()
        {
            // Same contract the obstacle loop already has: every candidate spawn
            // point (solo centre + every ring size up to MaxPlayers) must stay
            // clear. This wall is laid straight through the arena centre.
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Walls = new[] { new ArenaConfig.Wall
                { A = new Vector2(-5f, 0f), B = new Vector2(5f, 0f), HalfWidth = 0.8f } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("spawn point"));
        }

        [Test]
        public void Validate_WallOverRingSpawnPoint_NotJustSoloCenter_Throws()
        {
            // Fixwave Ф3 item 2: Validate_WallOverPlayerSpawn_Throws above lays
            // its wall through the ORIGIN, so it only ever exercises the
            // "solo center" clearance check — a mutant that deletes the
            // `for (int n = 2; n <= cfg.Arena.MaxPlayers; n++)` ring loop right
            // below it in ValidateWalls stays green against every existing
            // fixture. This wall sits far from the origin, straddling the P1
            // spawn point of a full 3-player lobby (Geometry.SpawnPosFor(1, 3,
            // ...) — the same formula the builder itself uses, not a restated
            // literal), and leaves the solo center untouched, so only the ring
            // loop can catch it.
            var (h, w, c, g, wv, a) = MakeDefaults();
            var arenaForSpawn = new ArenaSimConfig { Radius = a.Radius, PlayerSpawnRingFrac = a.PlayerSpawnRingFrac };
            float2 spawnP1 = Geometry.SpawnPosFor(1, 3, in arenaForSpawn);

            a.Walls = new[] { new ArenaConfig.Wall
            {
                A = new Vector2(spawnP1.x - 5f, spawnP1.y),
                B = new Vector2(spawnP1.x + 5f, spawnP1.y),
                HalfWidth = 0.8f
            } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("ring 3/point 1"));
        }

        [Test]
        public void Validate_WallsLockTheWholeWaveSpawnRing_Throws()
        {
            // A single wall thick enough to swallow the entire spawn ring: no
            // wave could ever place a mob, and the director would spin on
            // undischargeable debt for the whole match.
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = System.Array.Empty<ArenaConfig.Obstacle>();
            float ringRadius = a.Radius - wv.SpawnRingInset;
            a.Walls = new[] { new ArenaConfig.Wall
            {
                A = new Vector2(0f, 0.5f),
                B = new Vector2(0f, -0.5f),
                HalfWidth = ringRadius + 5f
            } };
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("spawn ring"));
        }

        [Test]
        public void Validate_PerPlayerCountFracNegative_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            wv.PerPlayerCountFrac = -0.1f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
            Assert.That(ex.Message, Does.Contain("PerPlayerCountFrac"));
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
            Assert.AreEqual(e.WavePause, a.WavePause, Eps);
            Assert.AreEqual(e.SpawnRingInset, a.SpawnRingInset, Eps);
            Assert.AreEqual(e.MinSpawnDistanceToPlayer, a.MinSpawnDistanceToPlayer, Eps);
            Assert.AreEqual(e.BaseCount, a.BaseCount);
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
    }
}
