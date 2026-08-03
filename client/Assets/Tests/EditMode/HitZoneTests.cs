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
            var c = TestConfigs.Open();
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
            // the balance premise this fixture rests on, asserted not assumed
            Assert.GreaterOrEqual(cfg.Weapon.Damage * cfg.Gunner.HeadDamageMult, cfg.Gunner.MaxHp);

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(TargetX, 0f)));
            float headBand = 0.5f * (cfg.Gunner.BodyTop + cfg.Gunner.HeadTop);
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
            float headshot = cfg.Weapon.Damage * cfg.Chaser.HeadDamageMult;
            Assert.Less(headshot, cfg.Chaser.MaxHp);                 // one is not enough
            Assert.GreaterOrEqual(2f * headshot, cfg.Chaser.MaxHp);   // two are

            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(TargetX, 0f)));
            float headBand = 0.5f * (cfg.Chaser.BodyTop + cfg.Chaser.HeadTop);

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
            float headBand = 0.5f * (cfg.Chaser.BodyTop + cfg.Chaser.HeadTop);
            TestWorlds.FireAimed3D(w, float2.zero, headBand, new float2(TargetX, 0f), headBand);

            w.ClearEvents();
            w.Tick(default);

            SimEvent hit = Blow(w, SimEventKind.ProjectileHit);
            Assert.AreEqual(HitZone.Head, hit.Zone);
            Assert.AreNotEqual(cfg.Weapon.Damage, hit.Amount); // NOT the projectile's base damage
            Assert.AreEqual(cfg.Weapon.Damage * cfg.Chaser.HeadDamageMult, hit.Amount, 1e-4f);
            // and the Hp the mob actually lost is that same post-multiplier number
            Assert.AreEqual(cfg.Chaser.MaxHp - hit.Amount, w.Mobs[0].Hp, 1e-4f);
            Assert.AreEqual(1f, hit.HitDir.x, 1e-4f); // travelling +X
        }
    }
}
