using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileHeightTests
    {
        /// D15 shot geometry — the hero fires from muzzle height at a point in
        /// the Gunner's head band 9 m out, which puts the straight trajectory
        /// over a Chaser's crown at 6.5 m and under it at 2 m. Their relation to
        /// the config is asserted in the fixtures below rather than assumed.
        ///
        /// app-88jb T14 (coordinator Ruling 74): `AimH` IS NO LONGER A LITERAL
        /// AND NO LONGER 3.1. It is read off the Gunner's own HEAD PART, because
        /// the column this fixture was written against is not what resolves a
        /// hit any more, and 3.1 m stopped meaning "the gunner's head" the day
        /// the parts landed: the gunner's head belt is [3.24, 4.20] and 3.1 is
        /// his TORSO. The number also has to clear a taller chaser — the model
        /// is 2.70 m where the column ended at 1.85 — so the aim is taken at
        /// 80 % of the way up the belt rather than at its middle. Measured: at
        /// 4.008 m the trajectory stands at 2.965 m where it enters the
        /// screening chaser's torso circle (x = 5.88), which clears his crown
        /// plus the round's own radius, 2.70 + 0.12 = 2.82, by 0.145 m. The
        /// middle of the belt (3.72) would leave only 2.777 there — INSIDE the
        /// graze forgiveness, i.e. the screen would eat the round and the test
        /// would witness nothing. Both users of this constant are checked
        /// against it by their own premises below.
        const float MuzzleH = 1f, GunnerX = 9f;
        static readonly float AimH = AimAtTheGunnersHead();

        /// 80 % of the way up the Gunner's head part — see AimH's own note for
        /// why the middle of the belt is not enough. A static helper rather than
        /// an initializer expression so the derivation is readable, and so it
        /// reads the SAME fixture the tests build (TestConfigs.OpenField's
        /// Gunner is TestConfigs.Default's Gunner — SpawnPair only zeroes speeds).
        static float AimAtTheGunnersHead()
        {
            HitPart head = TestConfigs.Default().Gunner.Parts[^1];
            return head.Bottom + 0.8f * (head.Top - head.Bottom);
        }

        /// A Chaser screening the line of fire plus the Gunner behind it, both
        /// frozen where they spawn: this pair measures the height gate, so a
        /// Chaser closing in (or a Gunner orbiting at StrafeSpeed) must not drag
        /// the sweep entry away from the geometry above.
        static SimulationWorld SpawnPair(float chaserX, float gunnerX, out SimConfig cfg)
        {
            cfg = TestConfigs.OpenField();
            cfg.Chaser.MaxSpeed = 0f;
            cfg.Gunner.MaxSpeed = 0f;
            cfg.Gunner.StrafeSpeed = 0f;
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(chaserX, 0f)),
                (MobType.Gunner, new float2(gunnerX, 0f)));
            return w;
        }

        [Test]
        public void Projectile_WithVelZ_AdvancesHeightPerTick()
        {
            var w = new SimulationWorld(1, TestConfigs.OpenField());
            w.SpawnProjectileForTest(ProjectileOwner.Player,
                new float2(0f, 0f), new float2(10f, 0f),
                height: 1f, velZ: -3f, damage: 1f, radius: 0.1f, ttl: 5f);
            w.Tick(new SimInput());
            var p = w.GetProjectileForTest(0);
            Assert.AreEqual(1f - 3f * SimulationWorld.TickDt, p.Height, 1e-5f);
            Assert.AreEqual(1f, p.PrevHeight, 1e-5f);
        }

        [Test]
        public void GunnerHeadOverCrowd_HitFromFarChaser()
        {
            // D15 + M5: the trajectory clears the Chaser at 6.5 m, so that
            // candidate is rejected on height and the scan carries on to the
            // Gunner at 9 m, whose head band the shot is aimed into.
            //
            // app-88jb T14 (Ruling 74): BOTH NUMBERS IN THAT SENTENCE MOVED, and
            // the premises below now read them off the PARTS instead of the
            // vertical zone column (which Т15 deleted). What is cleared is the chaser's
            // own crown, 2.70 m (his model, measured in session 43) rather than
            // his column's 1.85 m, and the entry height that clears it is
            // 2.965 m rather than the 2.37 m this comment used to quote. The
            // subject is untouched: a screening body is REJECTED ON HEIGHT and
            // the min-scan carries on to the target behind it — the M5 rescan
            // branch, which nothing else in the suite drives.
            var w = SpawnPair(6.5f, GunnerX, out SimConfig cfg);
            HitPart gunnerHead = cfg.Gunner.Parts[^1];
            HitPart chaserCrown = cfg.Chaser.Parts[^1];
            Assert.That(AimH, Is.InRange(gunnerHead.Bottom, gunnerHead.Top),
                "фикстура: прицел обязан лежать в поясе ГОЛОВЫ ганнера, иначе тест не о хедшоте");
            // THE CLEARANCE, STATED AS GEOMETRY RATHER THAN AS PROSE (the old
            // comment asserted it in words and went stale the moment the bodies
            // grew). The height where the round enters the screening chaser's
            // widest circle must stand above his crown plus the round's own
            // radius — that sum is exactly what HitZones.Resolve forgives.
            const float screenX = 6.5f;
            float screenEntryX = screenX - (cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius);
            float screenEntryH = MuzzleH + (AimH - MuzzleH) * screenEntryX / GunnerX;
            Assert.Greater(screenEntryH, chaserCrown.Top + cfg.Weapon.ProjectileRadius,
                "фикстура: трасса не проходит над экранирующим чейзером — рескан по высоте нечем показать");
            Assert.GreaterOrEqual(cfg.Weapon.Damage * gunnerHead.DamageMult, cfg.Gunner.MaxHp,
                "фикстура: один хедшот обязан быть смертельным, иначе «достал ганнера» не читается смертью");

            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH, new float2(GunnerX, 0f), AimH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, w.MobCount);
            Assert.AreEqual(MobType.Chaser, w.Mobs[0].Type);
            // Stage 3 Task 5 (spec Р252, coordinator R-21 — legitimate consequence
            // of friendly fire, reproduced arithmetically before touching this
            // number): the Gunner sits at distance 9 m from the player, squarely
            // inside its own PreferredRange +- RangeTolerance ([7.5, 10.5]), with
            // clear LoS (Open() — no obstacles) and FireCooldown starting at 0 —
            // it fires its own round back at the player BEFORE the player's
            // headshot kills it, and that round's straight line back to the
            // player passes through the very Chaser screening it (6.5 m out, same
            // y = 0 line). Friendly fire lets THAT round connect, for the
            // Gunner's own ProjectileDamage (8) — MaxHp 30 - 8 = 22, not the old
            // "passed over, never touched" 30.
            Assert.AreEqual(cfg.Chaser.MaxHp - cfg.Gunner.ProjectileDamage, w.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void CloseChaser_ScreensGunnerHead()
        {
            // the same shot 4.5 m earlier: at the Chaser's sweep entry x = 1.38
            // the trajectory is still low — inside the Chaser's body — so it
            // eats the round and the Gunner behind it is never reached.
            // app-88jb T14 (Ruling 74): with AimH read off the gunner's head
            // part the entry height here is 1.461 m rather than the 1.32 m this
            // comment used to quote, and it lands in the chaser's TORSO part
            // [0.88, 2.12) — the same Body zone and the same body multiplier
            // the expectation below has always been written from. The premise
            // makes that explicit instead of leaving it to the prose.
            var w = SpawnPair(2f, GunnerX, out SimConfig cfg);
            HitPart torso = cfg.Chaser.Parts[^2];
            const float screenX = 2f;
            float screenEntryH = MuzzleH + (AimH - MuzzleH)
                * (screenX - (cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius)) / GunnerX;
            Assert.That(screenEntryH, Is.InRange(torso.Bottom, torso.Top),
                "фикстура: экран ловит раунд не КОРПУСОМ — множитель ниже посчитан не от той части");
            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH, new float2(GunnerX, 0f), AimH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(2, w.MobCount); // nobody died
            // Stage 3 Task 5 (spec Р252, coordinator R-21 — same mechanism as
            // GunnerHeadOverCrowd_HitFromFarChaser above, reproduced
            // arithmetically): the Chaser screens the player's own round (body
            // hit, MaxHp - Weapon.Damage * the torso part's multiplier) exactly as before —
            // but the Gunner it shielded SURVIVES that screening (never hit by
            // the player at all) and is itself in range (9 m) with clear LoS, so
            // it fires its own round back at the player. That round's straight
            // line passes through the SAME screening Chaser (2 m out, same
            // y = 0 line) a second time, landing the Gunner's own
            // ProjectileDamage (8) on top of the player's body hit.
            Assert.AreEqual(cfg.Chaser.MaxHp - cfg.Weapon.Damage * torso.DamageMult
                - cfg.Gunner.ProjectileDamage, w.Mobs[0].Hp, 1e-4f);
            Assert.AreEqual(cfg.Gunner.MaxHp, w.Mobs[1].Hp, 1e-4f); // the Gunner is untouched
        }

        [Test]
        public void Graze_AtTheCrownPlusRadius_HitsAsHead()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Chaser.MaxSpeed = 0f;
            // app-88jb T14 (Ruling 74): THE CROWN IS THE MODEL'S, NOT THE
            // COLUMN'S. The chaser's old zone top was 1.85 and after this task
            // that height is the middle of his TORSO — a shot there is an
            // ordinary body hit, not a graze, and the test would have been
            // asserting Head on a chest shot. RENAMED IN Т15 for the same
            // reason (precedent: ProtocolVersion_Current_IsPinnedToThree ->
            // ...ToFour): the old name quoted a field that no longer exists.
            // The part's own Top is 2.70, so the
            // grazing line is 2.82 and the clearing line just above it. What the
            // test witnesses is unchanged and is the only witness of it in the
            // suite: the edge forgiveness HitZones.Resolve inherited from
            // Classify, which pulls a round grazing the crown back ONTO the
            // crown instead of dropping it off the table.
            float column = cfg.Chaser.Parts[^1].Top + cfg.Weapon.ProjectileRadius;

            var grazing = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(grazing, (MobType.Chaser, new float2(5f, 0f)));
            TestWorlds.FireAimed3D(grazing, float2.zero, column - 1e-4f,
                new float2(5f, 0f), column - 1e-4f);
            TestWorlds.RunUntilProjectilesDie(grazing);
            Assert.IsTrue(TestEvents.TryFirstOf(grazing, SimEventKind.ProjectileHit,
                out SimEvent graze), "the grazing shot did not register");
            Assert.AreEqual(HitZone.Head, graze.Zone); // clamped down onto the crown

            // one projectile radius higher and the column is genuinely cleared
            var over = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(over, (MobType.Chaser, new float2(5f, 0f)));
            TestWorlds.FireAimed3D(over, float2.zero, column + 1e-3f,
                new float2(5f, 0f), column + 1e-3f);
            TestWorlds.RunUntilProjectilesDie(over);
            Assert.AreEqual(0, TestEvents.CountOf(over, SimEventKind.ProjectileHit));
            Assert.AreEqual(1, over.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, over.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void FloorHit_BlocksAtFloorPoint_MobBehindUnharmed() // D4
        {
            // Angled-down shot: by construction (FireAimed3D) the trajectory
            // crosses ground height (h = 0) at x = 4 m. The floor stops the
            // round a projectile-radius short of that — well before the Chaser
            // parked at x = 6 m — so the mob behind the contact point is
            // untouched (Task 7 self-review: floor-vs-mob ordering).
            var cfg = TestConfigs.OpenField();
            cfg.Chaser.MaxSpeed = 0f;
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH, new float2(4f, 0f), 0f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, w.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, w.Mobs[0].Hp, 1e-4f); // never hit
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked,
                out SimEvent blocked));
            // floor: no impact normal (D12/C5 — never a "≈0" heuristic, it IS zero)
            Assert.AreEqual(0f, blocked.HitDir.x, 1e-6f);
            Assert.AreEqual(0f, blocked.HitDir.y, 1e-6f);
            // contact height == Radius: that is exactly where the sphere's
            // underside reaches the ground plane (t_floor's defining equation)
            Assert.AreEqual(cfg.Weapon.ProjectileRadius, blocked.Height, 1e-4f);
        }

        [Test]
        public void WallBlock_CarriesNormalAndHeight()
        {
            // Flat shot (targetH == muzzleH keeps VelZ at 0, isolating the wall
            // path from the floor one) straight out to the ring wall: the
            // contact height stays pinned at muzzle height, and HitDir carries
            // the real SweepArena normal instead of the pre-Task 7 zero placeholder.
            var cfg = TestConfigs.OpenField();
            // Stage 2 Task 16: the ring has to stay INSIDE one round's reach
            // (ProjectileSpeed x ProjectileLifetime) or the shot simply expires
            // in flight and never reports a wall block at all — at Arena.Radius
            // 65 with the TestConfigs weapon (35 m/s x 1.5 s = 52.5 m) it does
            // not. Half the reach is comfortably inside it; expressed off the
            // weapon's own numbers, never as a literal.
            cfg.Arena.Radius = 0.5f * cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            // app-88jb Т19 (coordinator Ruling 94): this test's subject is the
            // NORMAL and the HEIGHT a wall block reports, so the round has to
            // actually die on the rim. At the shipped MaxRicochets 2 it
            // reflects off the boundary instead and no ProjectileBlocked is
            // emitted at all. Stated right here, beside the other thing this
            // fixture states about itself (the shrunk radius one line up) —
            // the rest of this file never reaches a barrier, so the number is
            // deliberately NOT pushed into a shared helper.
            //
            // ⚠ ONLY THE WEAPON'S NUMBER, AND THE MOB'S IS LEFT ALONE ON
            // PURPOSE (coordinator Ruling 99). A mob-owned round reads the
            // Gunner archetype's own MaxRicochets (Impact.RicochetNumbersFor),
            // so this fixture would owe that number too IF a mob-owned round
            // could reach the rim here. None can: the fixture is
            // TestConfigs.OpenField() (TestConfigs.cs:674) — Open() (:607) off
            // Quiet() (:592), whose own `c.Wave.FirstWaveDelay = 1e6f` (:595)
            // means no waves and therefore no gunner. A second zero would be a
            // line with no reader.
            // THE RULE: a fixture here that spawns a LIVE gunner must state its
            // own Gunner.MaxRicochets.
            cfg.Weapon.MaxRicochets = 0;
            var w = new SimulationWorld(1, cfg);
            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH,
                new float2(cfg.Arena.Radius, 0f), MuzzleH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked,
                out SimEvent blocked));
            // wall normal points back toward the arena centre, i.e. -x here
            Assert.AreEqual(-1f, blocked.HitDir.x, 1e-4f);
            Assert.AreEqual(0f, blocked.HitDir.y, 1e-4f);
            Assert.AreEqual(MuzzleH, blocked.Height, 1e-4f);
        }

        [Test]
        public void AimedShot_HitsExactPoint_IncludingFloor() // Task 15
        {
            // Fully settled aim (AimSettleTimer at its cap, QA1 seam) on a fresh
            // world (RecoilOffset still 0) leaves the effective cone at exactly
            // zero, so no spread is drawn at all and the round must fly along the
            // muzzle -> (AimPoint, AimHeight) line. Aimed at a point ON the floor,
            // that line's end IS the impact point — up to the one geometric offset
            // the floor test carries: it stops the sphere when its CENTRE reaches
            // Radius (ProjectileSystem's t_floor), i.e. Radius / tan(descent)
            // short of the aimed point along the line. The fixture shrinks
            // ProjectileRadius so that offset stays well inside the tolerance and
            // this assert measures aim accuracy, not sphere size.
            var cfg = TestConfigs.OpenField();
            cfg.Weapon.ProjectileRadius = 0.01f;
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.AimSettleTimer = cfg.Hero.AimSettleSeconds;
            w.SetPlayerForTest(p);

            var floorPoint = new float2(2f, 1.5f); // 2.5 m out, diagonal: both axes are asserted
            w.Tick(new SimInput { AimPoint = floorPoint, AimHeight = 0f,
                                  AimHeld = true, FireHeld = true });
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked,
                out SimEvent blocked));
            // floor contact: centre height == Radius, no surface normal (Task 7)
            Assert.AreEqual(cfg.Weapon.ProjectileRadius, blocked.Height, 1e-4f);
            Assert.AreEqual(0f, blocked.HitDir.x, 1e-6f);
            Assert.AreEqual(0f, blocked.HitDir.y, 1e-6f);
            Assert.AreEqual(floorPoint.x, blocked.Pos.x, 0.05f);
            Assert.AreEqual(floorPoint.y, blocked.Pos.y, 0.05f);
        }

        [Test]
        public void HipShot_HorizontalAtMuzzleHeight() // Task 15
        {
            // Hip fire keeps the flat Phase-1 geometry: the round leaves the
            // standing muzzle horizontally whatever height the (unheld) aim
            // carries — AimHeight is the aimed branch's input alone.
            var cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            w.Tick(new SimInput { AimPoint = new float2(10f, 0f),
                                  AimHeight = cfg.Hero.MaxAimHeight, FireHeld = true });

            Assert.AreEqual(1, w.ProjectileCount);
            ProjectileState shot = w.GetProjectileForTest(0);
            Assert.AreEqual(0f, shot.VelZ, 1e-6f);
            Assert.AreEqual(cfg.Hero.MuzzleHeight, shot.Height, 1e-4f);
            // the whole speed budget stays horizontal (nothing was spent climbing)
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed, math.length(shot.Vel), 1e-3f);
        }

        [Test]
        public void SlideFire_FromSlideMuzzleHeight() // Task 15
        {
            // Mid-slide the hero is low to the ground, so the shot leaves the
            // slide muzzle height instead of the standing one.
            var cfg = TestConfigs.OpenField();
            Assert.IsTrue(cfg.Weapon.CanFireWhileSlide,
                "fixture: this weapon must be allowed to fire mid-slide");
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration; // QA1 seam — no run-up choreography needed
            p.SlideDir = new float2(1f, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { AimPoint = new float2(10f, 0f), FireHeld = true });

            Assert.AreEqual(1, w.ProjectileCount);
            Assert.AreEqual(cfg.Hero.SlideMuzzleHeight, w.GetProjectileForTest(0).Height, 1e-4f);
        }

        [Test]
        public void EqualT_TieBreaksLowerIndex()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Chaser.MaxSpeed = 0f;
            var w = new SimulationWorld(1, cfg);
            // mirrored across the firing line (so both circles are entered at a
            // bit-identical t) and further apart than SeparationRadius (so the
            // pair never nudges itself out of that symmetry)
            float halfGap = 0.5f * cfg.Chaser.SeparationRadius + 0.1f;
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(5f, halfGap)),
                (MobType.Chaser, new float2(5f, -halfGap)));
            int lowSlotId = w.Mobs[0].Id;

            // one fat, overkill round wide enough to reach both circles at once
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), MuzzleH, 0f,
                cfg.Chaser.MaxHp * 10f, 0.6f, cfg.Weapon.ProjectileLifetime);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, w.MobCount); // single-target: no piercing
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out SimEvent died));
            Assert.AreEqual(lowSlotId, died.EntityId);
        }
    }
}
