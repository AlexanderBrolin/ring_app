using NUnit.Framework;
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

        static SimEvent Blow(SimulationWorld w, SimEventKind kind)
        {
            Assert.IsTrue(TestEvents.TryFirstOf(w, kind, out SimEvent found), $"no {kind} emitted");
            return found;
        }

        /// app-88jb T14: the middle of a body's HEAD, read off the body's own
        /// ORDERED STACK OF PARTS instead of off the LegsTop/BodyTop/HeadTop
        /// column. Written once here because three fixtures below need exactly
        /// this expression, and three copies of it is the shape rule 2 removes.
        ///
        /// WHY IT IS NOT `0.5f * (BodyTop + HeadTop)` ANY MORE. That was the
        /// same aim point expressed through the column, and until T14 the two
        /// agreed. They do not agree now, and the difference is the whole task:
        /// the column stopped at the chaser's 1.85 m while the model stands
        /// 2.70 m tall (measured, session 43), so `0.5 * (1.45 + 1.85)` = 1.65
        /// lands squarely in the TORSO part [0.88, 2.12) and the fixture would
        /// be asking for a headshot while shooting the chest. Read off the part,
        /// the aim is 2.41 m for a chaser and 3.72 m for a gunner -- inside the
        /// head belts [2.12, 2.70] and [3.24, 4.20] the bodies actually carry.
        static float HeadBandOf(HitPart[] parts)
        {
            HitPart head = parts[parts.Length - 1];
            return 0.5f * (head.Bottom + head.Top);
        }

        [Test]
        public void Zones_ClassifyLegsBodyHead_AtBoundaries()
        {
            var c = TestConfigs.Default().Chaser;
            Assert.AreEqual(HitZone.Legs, HitZones.Classify(c.LegsTop - 1e-4f, c.LegsTop, c.BodyTop, c.HeadTop));
            Assert.AreEqual(HitZone.Body, HitZones.Classify(c.LegsTop,         c.LegsTop, c.BodyTop, c.HeadTop));
            Assert.AreEqual(HitZone.Body, HitZones.Classify(c.BodyTop - 1e-4f, c.LegsTop, c.BodyTop, c.HeadTop));
            Assert.AreEqual(HitZone.Head, HitZones.Classify(c.BodyTop,         c.LegsTop, c.BodyTop, c.HeadTop));
            // clamped: a graze over the crown still reads as Head, a cut under
            // the floor line still reads as Legs
            Assert.AreEqual(HitZone.Head, HitZones.Classify(c.HeadTop + 0.2f,  c.LegsTop, c.BodyTop, c.HeadTop));
            Assert.AreEqual(HitZone.Legs, HitZones.Classify(-0.05f,            c.LegsTop, c.BodyTop, c.HeadTop));
        }

        [Test]
        public void MultFor_PicksThePerZoneNumber_AndNoneIsNeutral()
        {
            var c = TestConfigs.Default().Chaser;
            Assert.AreEqual(c.LegsDamageMult, HitZones.MultFor(HitZone.Legs,
                c.LegsDamageMult, c.BodyDamageMult, c.HeadDamageMult), 1e-6f);
            Assert.AreEqual(c.BodyDamageMult, HitZones.MultFor(HitZone.Body,
                c.LegsDamageMult, c.BodyDamageMult, c.HeadDamageMult), 1e-6f);
            Assert.AreEqual(c.HeadDamageMult, HitZones.MultFor(HitZone.Head,
                c.LegsDamageMult, c.BodyDamageMult, c.HeadDamageMult), 1e-6f);
            // "no zone" must never scale damage — melee and any future
            // zone-less source deal exactly what they say
            Assert.AreEqual(1f, HitZones.MultFor(HitZone.None,
                c.LegsDamageMult, c.BodyDamageMult, c.HeadDamageMult), 1e-6f);
        }

        [Test]
        public void Overlaps_AcceptsInsideTheRadiusPaddedColumn_RejectsOutside()
        {
            var c = TestConfigs.Default().Chaser;
            const float r = 0.12f;
            // a flat pass at body height
            Assert.IsTrue(HitZones.Overlaps(c.BodyTop, c.BodyTop, r, c.HeadTop));
            // grazing the crown / scraping the ground: the projectile's own
            // radius extends the column by r at both ends
            Assert.IsTrue(HitZones.Overlaps(c.HeadTop + r - 1e-4f, c.HeadTop + r - 1e-4f, r, c.HeadTop));
            Assert.IsTrue(HitZones.Overlaps(-r + 1e-4f, -r + 1e-4f, r, c.HeadTop));
            Assert.IsFalse(HitZones.Overlaps(c.HeadTop + r + 1e-3f, c.HeadTop + r + 1e-3f, r, c.HeadTop));
            Assert.IsFalse(HitZones.Overlaps(-r - 1e-3f, -r - 1e-3f, r, c.HeadTop));
            // a descending shot that only clips the column on part of the chord
            // still counts — the test is interval-vs-interval, not point-vs-interval
            Assert.IsTrue(HitZones.Overlaps(c.HeadTop + 5f, c.BodyTop, r, c.HeadTop));
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
            float legsBand = 0.5f * cfg.Chaser.LegsTop;
            TestWorlds.FireAimed3D(w, float2.zero, legsBand, new float2(TargetX, 0f), legsBand);

            w.ClearEvents();
            w.Tick(default);

            SimEvent hit = Blow(w, SimEventKind.ProjectileHit);
            Assert.AreEqual(HitZone.Legs, hit.Zone);
            float expected = cfg.Weapon.Damage * cfg.Chaser.LegsDamageMult;
            Assert.AreEqual(expected, hit.Amount, 1e-4f);
            Assert.AreEqual(cfg.Chaser.MaxHp - expected, w.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void Fist_ZoneBody_NoMult()
        {
            var cfg = Range();
            // a deliberately non-neutral Body multiplier on the hero: if the
            // telegraphed strike ever routed through the zone table, the damage
            // asserted below would come out doubled
            cfg.Hero.BodyDamageMult = 2f;
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

            const float shotHeight = 0.3f; // below SlideProfileTop (0.55) and LegsTop (0.55)
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
    }
}
