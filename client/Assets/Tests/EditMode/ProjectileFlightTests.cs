using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-88jb Т19 (spec §3.4, owner decision Н19): the round's ricochet off
    /// STATIC geometry, which repeats the dash's own rule off a wall one for
    /// one (PlayerMovementSystem.cs:349-351).
    ///
    /// THERE IS NO ANGLE THRESHOLD ANYWHERE IN THIS FILE, and that is the
    /// subject rather than an omission (finding C-C4): `dot(Vel, normal) < 0`
    /// means no more than "flying INTO the wall", exactly as it does for the
    /// dash. What bounds a chain of weak ricochets is the pair `MaxRicochets`
    /// and `RicochetMinSpeed`, and each has a test of its own below.
    ///
    /// A CONTACT THE RICOCHET GATE REFUSES STILL EXTINGUISHES THE ROUND
    /// (owner decision Р439). `dot(Vel, normal) < 0` is a condition INSIDE the
    /// gate and nowhere else: fail it — or the counter, or the speed floor —
    /// and the round falls into the same `ProjectileBlocked` arm it has always
    /// fallen into. Today's behavior is not changed by this task for any
    /// contact that does not reflect, which is why three of the eleven tests
    /// below are green before the ricochet exists at all.
    ///
    /// THREE OF THE ELEVEN ARE GUARDS, NOT WITNESSES, and they say so in their
    /// own names or docs (lesson 427) — each name whole, on its own line, so a
    /// sweep by name finds them:
    ///   `ProjectileFlyingAwayFromTheWall_DoesNotReflect`
    ///   `FloorDoesNotRicochet_Guard`
    ///   `ExpiredRoundDoesNotLiveOneExtraTickByRicocheting`
    /// All three are green on today's code, because today every contact
    /// extinguishes the round and that is exactly what they demand. They earn
    /// their place against the MUTANT, not against the stub.
    ///
    /// WHERE A REFLECTED ROUND LANDS IS THE CONTACT PLUS ONE SKIN ALONG THE
    /// NORMAL (coordinator Ruling 96), never the bare contact — see
    /// `RicochetedRound_DoesNotSinkThroughTheWall`, which states the arithmetic
    /// with the addresses of the idiom it follows.
    public class ProjectileFlightTests
    {
        /// EXPLICIT fixture, built from `Quiet()` rather than from `Open()` or
        /// from `Default()`, and both exclusions are facts about those two
        /// rather than taste:
        ///
        ///  - `TestConfigs.Open()` sets `ObstacleCount = 0` and
        ///    `ObstaclePos = Array.Empty<float2>()`, so the
        ///    `cfg.Arena.ObstaclePos[0]` every BARRIER test below reads would
        ///    throw IndexOutOfRangeException — the fixture could not state a
        ///    barrier at all (finding A-C5). THE EXCLUSION IS ABOUT THE
        ///    OBSTACLE, NOT ABOUT `Open()` ITSELF, and the difference became
        ///    load-bearing the moment this file grew a test that reads no
        ///    obstacle: the ring boundary's, whose whole subject is the arena's
        ///    rim, is built ON `Open()` for the very property stated here — an
        ///    emptied interior is what leaves the rim the first static geometry
        ///    a round can meet. Its base is chosen in `RingBoundaryFixture`
        ///    below, which carries the argument beside itself so neither
        ///    reading of this bullet is left to the reader;
        ///  - `TestConfigs.Default()` keeps its waves, and a gunner's rounds
        ///    land in the very `w.ProjectileCount` the loops below exit on, so
        ///    `ThirdContact…`'s bound would stop being about the ricochet
        ///    counter and start being about the wave schedule (finding Г-I1).
        ///
        /// `Quiet()` is exactly `Default()` with
        /// `Wave.FirstWaveDelay = 1e6f`: the whole of `DefaultArena()` — twenty
        /// obstacles, the interior walls, the two zone-wall arcs and the rim —
        /// with no waves at all.
        ///
        /// The three numbers are stated HERE, in the fixture, and not taken
        /// from `TestConfigs.Default()`: the shipped value of
        /// `Default().Weapon.MaxRicochets` is a separate decision with an
        /// inventory of its own behind it, and no test in this file may depend
        /// on which way it goes.
        static SimConfig Fixture(int maxRicochets = 2, float minSpeed = 6f)
        {
            SimConfig cfg = TestConfigs.Quiet();
            cfg.Weapon.MaxRicochets = maxRicochets;
            cfg.Weapon.RicochetRetention = 0.8f;
            cfg.Weapon.RicochetMinSpeed = minSpeed;
            return cfg;
        }

        /// THE RIM'S OWN FIXTURE, and the ONE place in this file where
        /// `TestConfigs.Open()` is the right base rather than the refused one.
        /// The argument that keeps every other fixture here on `Quiet()` is an
        /// argument about the OBSTACLE — `Open()` empties `ObstaclePos`, so a
        /// test that reads `ObstaclePos[0]` cannot be built on it — and this
        /// test reads no obstacle at all. What it needs is the opposite
        /// property: an interior with nothing in it, so that the arena's outer
        /// boundary is the first and only static geometry a round leaving the
        /// center can meet, with no obstacle circle, interior wall or zone arc
        /// to take the min-scan's slot ahead of it.
        ///
        /// THE ARENA IS SWAPPED AND THE THREE RICOCHET NUMBERS ARE NOT
        /// RESTATED. `Fixture()` above is the one home of those three, for the
        /// reason its own doc gives, and `Open()` differs from the `Quiet()`
        /// underneath `Fixture()` in ARENA FIELDS ALONE — the obstacles, the
        /// interior walls and the zone-wall arcs it drops are all of them
        /// Arena's. So taking `Fixture()` whole and replacing its `Arena` gives
        /// `Open()`'s world carrying `Fixture()`'s numbers, and not one number
        /// is written down twice.
        ///
        /// TWENTY METERS, AND THROUGH `TestConfigs.ShrinkArena` RATHER THAN BY
        /// ASSIGNING `Arena.Radius`. The shipped rim stands at 173 m, which a
        /// 35 m/s round reaches in about 148 ticks against the 18 this fixture
        /// costs; and the helper is what moves the zone boundaries INWARD with
        /// the world, so the fixture never carries boundaries wider than the
        /// arena that is supposed to bound them — the very defect that helper
        /// was written for.
        static SimConfig RingBoundaryFixture()
        {
            SimConfig cfg = Fixture();
            cfg.Arena = TestConfigs.Open().Arena;
            TestConfigs.ShrinkArena(ref cfg, 20f);
            return cfg;
        }

        [Test]
        public void ProjectileFlyingAwayFromTheWall_DoesNotReflect()
        {
            // The condition `dot(Vel, normal) < 0` means no more than "flying
            // INTO the wall", and under Р439 it lives INSIDE the ricochet gate
            // and nowhere else: a contact that fails it is not "unresolved", it
            // is extinguished exactly as it has always been. So the ending this
            // test demands is today's ending — the round is GONE and one
            // ProjectileBlocked stands for it.
            //
            // A GUARD ON THE STUB, A WITNESS ON THE MUTANT, and the two are not
            // the same claim (lesson 427): with no ricochet in the code at all
            // this passes, because the stub extinguishes every contact. Its
            // whole worth is against mutation M14, "reflect with no `dot < 0`
            // test", and against that mutant it is decisive — see the
            // arithmetic below.
            //
            // THE FIXTURE MUST CONTAIN A CONTACT (finding D-C4): a first draft
            // fired into an EMPTY FIELD, where M14 changes nothing at all —
            // there is no contact point to reflect at, and the test was green
            // on the mutant and on the stub alike. Here the round is born
            // INSIDE the obstacle's padded circle and flies AWAY from it:
            // Geometry.SegmentCircle's own start-inside branch
            // (Geometry.cs:26) answers `t = 0` and SweepArena takes the outward
            // radial at the birth point as the normal (SweepArena's own
            // obstacle branch), so
            // the contact is real and its `dot` is POSITIVE.
            //
            // WHY M14 SURVIVES HERE AND IS CAUGHT BY THE FIRST ASSERT, in the
            // fixture's own numbers: the birth point stands 2.26 m from the
            // obstacle's center against a padded radius of 2.32 (2.2 + 0.12),
            // so `t = 0`; the normal is (+1, 0) and `dot(Vel, normal) = +35`.
            // Strike the `dot < 0` test and the two REMAINING gates both pass —
            // `Ricochets 0 < MaxRicochets 2`, and the damped speed
            // 35 x 0.8 = 28 clears `RicochetMinSpeed 6` with room to spare — so
            // the mutant reflects, lands at the contact plus a skin
            // (Ruling 96), and the round SURVIVES the tick with Vel (-28, 0).
            // `AreEqual(0, w.ProjectileCount)` is what dies on it.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float2 birth = obstacle + new float2(r + cfg.Weapon.ProjectileRadius * 0.5f, 0f);
            Assert.Less(math.distance(birth, obstacle), r + cfg.Weapon.ProjectileRadius,
                "fixture premise: the round is born INSIDE the obstacle's padded circle (t = 0)");
            // The two gates M14 does NOT touch have to be open, or the mutant
            // would be stopped by one of them and this test would pin nothing.
            Assert.GreaterOrEqual(cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention,
                cfg.Weapon.RicochetMinSpeed,
                "fixture premise: the speed floor does not stop the mutant, so only dot does");
            Assert.Greater(cfg.Weapon.MaxRicochets, 0,
                "fixture premise: the counter does not stop the mutant either");
            w.SpawnProjectileForTest(ProjectileOwner.Player, birth,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 0.5f);

            w.Tick(default);

            Assert.AreEqual(0, w.ProjectileCount,
                "снаряд, летящий ОТ стены, отразился и выжил — условие dot < 0 снято");
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "контакт, отвергнутый гейтом рикошета, обязан гасить снаряд, как и до Т19 (Р439)");
        }

        [Test]
        public void SpeedAfterRicochet_IsMultipliedByRetention()
        {
            // The expectation is a NUMBER out of the fixture, never "roughly
            // slower": the damped 3D speed is the shot's own speed times
            // `RicochetRetention`, and `VelZ` is damped by the same factor so
            // the 3D direction survives the horizontal reflection.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float2 from = obstacle - new float2(r + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 6 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++) w.Tick(default);

            Assert.AreEqual(1, w.Projectiles[0].Ricochets, "отскока не случилось");
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention,
                math.length(new float3(w.Projectiles[0].Vel, w.Projectiles[0].VelZ)), 0.05f,
                "скорость после отскока не умножена на RicochetRetention");
            Assert.Less(w.Projectiles[0].Vel.x, 0f, "снаряд не развернулся");
        }

        /// THE ONLY INCLINED SHOT IN THIS FILE THAT REACHES A REFLECTION, and
        /// that is the whole of why it stands beside the level one above rather
        /// than folding into it. Every other round here is launched with
        /// `velZ: 0f`, and the one that is not -- the floor guard, which
        /// descends at -20 m/s -- exists precisely to end at a contact the
        /// ricochet refuses. A LEVEL
        /// ROUND MAKES THREE OF THE RICOCHET'S OWN WRITES UNOBSERVABLE AT ONCE:
        /// with `VelZ` at zero the contact height EQUALS the pre-step height,
        /// so "leave `Height` where it stood" and "land it on the contact" are
        /// the same number; damping a zero changes nothing; and `PrevHeight`
        /// ends at that same constant whichever value it is handed. Tilt the
        /// shot and all three come apart.
        ///
        /// THE THREE CLAIMS ARE ONE TEST AND NOT THREE, deliberately: they need
        /// the SAME inclined contact, and splitting them would state this
        /// fixture -- and re-derive its contact tick -- three times over for no
        /// gain in what is pinned.
        ///
        /// THE ROUND MEETS THE BARRIER, NOT THE GROUND, and the fixture shows
        /// it with its own numbers instead of asserting it as intent (the
        /// premise below divides the two gaps by the two rates). At 35 m/s the
        /// round closes the 2.88 m to the barrier's padded circle in under
        /// three ticks; at -6 m/s it sheds 0.2 m a tick of the 2.88 m that
        /// separate its birth height from the ground, and would need fourteen.
        /// A steeper descent would quietly turn this into a second floor test,
        /// where there is no modelled normal to reflect about at all.
        ///
        /// THE HEIGHT CLAIM IS READ OFF THE HORIZONTAL, AND THAT IS THE WHOLE
        /// TRICK OF IT. A claim of the shape "somewhere between the pre-step
        /// height and the step's end" would die on a `Height` left standing but
        /// SURVIVE one moved by the whole step, and those two mutants are
        /// exactly the pair this claim exists to kill. So the fraction of the
        /// step the contact sits at is recovered from the axis this test does
        /// not otherwise measure: the landing is the contact pushed one
        /// `Geometry.Skin` along the normal (Ruling 96), the normal of a
        /// head-on shot into a circle is (-1, 0), and undoing that push gives
        /// the contact, whose x against the pre-step x gives the fraction. The
        /// vertical is then checked against that fraction. This is not the
        /// implementation written out a second time -- two INDEPENDENT axes of
        /// one contact are compared -- and the premise that the fraction lies
        /// STRICTLY inside (0, 1) is what keeps the two mutants from coinciding.
        [Test]
        public void InclinedRound_KeepsItsDescent_AndLandsAtTheHeightItMetTheWallAt()
        {
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float pr = cfg.Weapon.ProjectileRadius;
            float2 from = obstacle - new float2(r + 3f, 0f);
            const float launchHeight = 3f;
            const float launchVelZ = -6f;
            // Head-on, which is what makes the surface normal EXACTLY (-1, 0)
            // rather than nearly so -- the reconstruction below leans on that
            // and would be reading a made-up number without it.
            Assert.AreEqual(obstacle.y, from.y, 0f,
                "fixture premise: the shot runs through the obstacle's own center, so the "
                + "contact normal is exactly (-1, 0)");
            float stepPerTick = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float gapToBarrier = math.distance(from, obstacle) - (r + pr);
            float dropPerTick = -launchVelZ * SimulationWorld.TickDt;
            Assert.Greater((launchHeight - pr) / dropPerTick, gapToBarrier / stepPerTick,
                "fixture premise: the barrier arrives many ticks before the ground does, so "
                + "this is a barrier contact and not a second floor test");
            Assert.GreaterOrEqual(
                math.length(new float3(cfg.Weapon.ProjectileSpeed, 0f, launchVelZ))
                    * cfg.Weapon.RicochetRetention,
                cfg.Weapon.RicochetMinSpeed,
                "fixture premise: the damped 3D speed still clears the floor, so the round "
                + "reflects and the three claims below measure a reflection");
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f),
                height: launchHeight, velZ: launchVelZ,
                damage: 1f, radius: pr, ttl: 1f);

            // The pre-step state is taken INSIDE the loop, once per tick, so
            // what the claims below read is the state going into the tick the
            // contact actually fell on -- not a tick number guessed in advance.
            float2 prePos = float2.zero;
            float2 preVel = float2.zero;
            float preHeight = 0f;
            float preVelZ = 0f;
            for (int i = 0; i < 6 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++)
            {
                prePos = w.Projectiles[0].Pos;
                preVel = w.Projectiles[0].Vel;
                preHeight = w.Projectiles[0].Height;
                preVelZ = w.Projectiles[0].VelZ;
                w.Tick(default);
            }

            Assert.AreEqual(1, w.Projectiles[0].Ricochets, "отскока не случилось");
            Assert.AreNotEqual(0f, preVelZ,
                "fixture premise: the round really was descending into the tick it reflected on");

            Assert.AreEqual(preVelZ * cfg.Weapon.RicochetRetention, w.Projectiles[0].VelZ, 1e-6f,
                "вертикальная скорость после отскока не умножена на RicochetRetention — "
                + "3D-направление не пережило отражение");

            float2 normal = new float2(-1f, 0f);
            float2 contact = w.Projectiles[0].Pos - normal * Geometry.Skin;
            float t = (contact.x - prePos.x) / (preVel.x * SimulationWorld.TickDt);
            Assert.Greater(t, 0f,
                "fixture premise: the contact falls after the step's start, so the height it "
                + "lands at is not simply the pre-step one");
            Assert.Less(t, 1f,
                "fixture premise: and STRICTLY before the step's end, so a height moved by the "
                + "whole step is a different number from the one this claim demands");
            Assert.AreEqual(preHeight + preVelZ * SimulationWorld.TickDt * t,
                w.Projectiles[0].Height, 1e-4f,
                "высота после отскока не села на контактную — раунд либо застыл по вертикали, "
                + "либо проехал весь шаг");
            Assert.AreEqual(preHeight, w.Projectiles[0].PrevHeight, 0f,
                "PrevHeight не равна дошаговой высоте — вертикальная пара разъехалась с Pos/PrevPos");
        }

        [Test]
        public void ThirdContact_ExtinguishesTheRound_WhenMaxRicochetsIsTwo()
        {
            // The COUNTER is what bounds the chain, not an angle. The round is
            // handed a spent counter through the seam rather than made to earn
            // it, so this test states one thing only: at `Ricochets ==
            // MaxRicochets` the next contact extinguishes.
            SimConfig cfg = Fixture(maxRicochets: 2);
            var w = new SimulationWorld(7, cfg);
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 30f);
            var p = w.Projectiles[0]; p.Ricochets = 2; w.SetProjectileForTest(0, p);
            for (int i = 0; i < 400 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount,
                "снаряд с исчерпанным счётчиком отскочил третий раз");
        }

        [Test]
        public void SlowRound_Extinguishes_InsteadOfRicocheting()
        {
            // `RicochetMinSpeed`, the other half of the bound. The threshold is
            // set deliberately ABOVE any speed the damping could leave, so the
            // difference this test measures is structural rather than an edge
            // case one rounding decision away.
            SimConfig cfg = Fixture(minSpeed: 1e6f);
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float2 from = obstacle - new float2(cfg.Arena.ObstacleRadius[0] + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 6 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount, "медленный снаряд отскочил, а не погас");
        }

        [Test]
        public void FloorDoesNotRicochet_Guard()
        {
            // A GUARD, GREEN ON TODAY'S CODE (lesson 427), and named one rather
            // than passed off as a witness: the floor has no modelled normal,
            // so it must go on extinguishing the round after the ricochet
            // lands. It earns its place against the mutant that reflects off
            // every contact, not against the stub.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(5f, 0f), height: 1f, velZ: -20f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius, ttl: 1f);
            for (int i = 0; i < 10 && w.ProjectileCount > 0; i++) w.Tick(default);
            Assert.AreEqual(0, w.ProjectileCount, "снаряд отскочил от пола");
        }

        [Test]
        public void RicochetedRound_DoesNotSinkThroughTheWall()
        {
            // WHERE the ricocheted round stands, which is a different question
            // from how fast it travels. The reflected velocity applies from the
            // NEXT tick (decision Р376), so the position this tick ends at is
            // built from the CONTACT and not from the step's own end, which
            // lies inside the obstacle's body.
            //
            // AND IT IS THE CONTACT PLUS ONE SKIN ALONG THE NORMAL, not the
            // bare contact (coordinator Ruling 96). The reason is float, not
            // taste: Geometry.SegmentCircle's start-inside test is a STRICT
            // `<` (Geometry.cs:26), so a point exactly on the padded circle
            // counts as outside — but a lerped contact does not land on
            // "exactly", and a landing a ULP inside would answer `t = 0` on the
            // very next tick with the OUTWARD normal. Under Р439 such a contact
            // is not left unresolved: it fails the `dot < 0` gate and
            // EXTINGUISHES the round, so a skinless landing would kill the
            // round on its own touchdown point. The idiom is this project's
            // own, in the very file the arithmetic comes from:
            // `pos = c + normal * (r + Skin)` (Geometry.PushOutOfCircle,
            // Geometry.cs:593) and the same line for the stadium shape
            // (Geometry.cs:627); `Skin = 1e-3f` (Geometry.cs:8).
            //
            // So the assertion is the distance to the obstacle's center against
            // the PADDED radius — that is what "did not sink into the wall"
            // means with no dependence on which direction the round left in —
            // and a second tick follows it, because "landed clear" and "still
            // alive a tick later" are two different claims and only the pair
            // covers the skin.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float pr = cfg.Weapon.ProjectileRadius;
            float2 from = obstacle - new float2(r + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: pr, ttl: 1f);
            // The step is longer than the gap the contact leaves, which is what
            // makes "the step's end" and "the contact" two different points at
            // all — stated rather than assumed, off the fixture's own numbers.
            float stepLength = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            Assert.Greater(stepLength, pr,
                "fixture premise: a tick's step is long enough for the step's end to sit "
                + "measurably deeper than the contact");
            for (int i = 0; i < 6 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++) w.Tick(default);

            Assert.AreEqual(1, w.Projectiles[0].Ricochets, "отскока не случилось");
            Assert.GreaterOrEqual(math.distance(w.Projectiles[0].Pos, obstacle), r + pr,
                "отскочивший снаряд утонул в барьере — позиция взята с конца шага, а не с контакта");

            w.Tick(default);

            Assert.AreEqual(1, w.ProjectileCount,
                "снаряд погиб о собственную точку посадки — контакт не отодвинут на Geometry.Skin");
        }

        /// THE ONE TEST THAT EARNS A SECOND RICOCHET RATHER THAN STATING IT.
        /// `ThirdContact_ExtinguishesTheRound_WhenMaxRicochetsIsTwo` above
        /// hands the counter a spent value through the seam and never executes
        /// an increment at all (the plan's own finding D-I5), so without this
        /// one nothing in the suite would show the counter climbing 0 -> 1 -> 2
        /// off real contacts, and nothing would show that a round which has
        /// already ricocheted is still an ordinary round to the next barrier.
        ///
        /// IT REPLACED A "CORNER" TEST, AND THE REPLACEMENT IS A RULING, NOT A
        /// RETREAT (coordinator Ruling 95). The corner this file was asked for
        /// — two barriers tight enough that the ricochet's own contact point
        /// lands INSIDE the second one — is not constructible, and the
        /// arithmetic says so rather than the author: the gather holds ONE
        /// interior-barrier slot, filled by SweepArena's MINIMUM `t` over every
        /// obstacle, wall and arc (Geometry.cs:779-861), so a second barrier
        /// whose padded circle contains a point of the step is entered no LATER
        /// than that point and would have won the slot — the first ricochet
        /// would never have happened. Only exact equality is left, and Р376
        /// keeps the reflected velocity for the NEXT tick, so the round does
        /// not travel into the second barrier during this one either. The
        /// "contact inside another barrier" check therefore has no reachable
        /// case and is NOT written; a branch with no witness is what this epic
        /// has now refused three times (M-guard Т15, Ruling 88 Т17, app-rahx
        /// Т18). What survives of the fixture is the reachable half, below.
        [Test]
        public void SecondRicochet_IsEarned_AndNeitherBarrierIsLeakedThrough()
        {
            // Two barriers facing each other across the round's own line. The
            // round ricochets off the FAR one, travels back into the NEAR one,
            // ricochets off that, and is extinguished on its third contact with
            // the counter spent. Two claims, both stated:
            //   - the counter reached TWO off real contacts, not off a seam;
            //   - at no point did the round stand deeper than a barrier's own
            //     surface, which is what "leaked through" would look like as a
            //     POSITION rather than as an event.
            SimConfig cfg = Fixture(maxRicochets: 2);
            float pr = cfg.Weapon.ProjectileRadius;
            float2 far = cfg.Arena.ObstaclePos[0];
            float rFar = cfg.Arena.ObstacleRadius[0];
            float2 launch = far - new float2(rFar + 3f, 0f);
            // The near barrier is stated off the launch point, so the clearance
            // in front of it is a fixture number rather than a coordinate: the
            // round must start OUTSIDE it, or the round would resolve against
            // it on tick one and never reach the far barrier at all.
            float rNear = rFar;
            const float clearance = 0.5f;
            float2 near = launch - new float2(rNear + pr + clearance, 0f);
            // Two obstacles and nothing else of DefaultArena's twenty: the
            // remaining eighteen stand tens of meters away and would only make
            // the premises below harder to read. The walls, the arcs and the
            // rim are left exactly as they are.
            cfg.Arena.ObstacleCount = 2;
            cfg.Arena.ObstaclePos = new[] { far, near };
            cfg.Arena.ObstacleRadius = new[] { rFar, rNear };

            Assert.Greater(math.distance(launch, near), rNear + pr,
                "fixture premise: the round starts clear of the near barrier");
            Assert.Greater(math.distance(launch, far), rFar + pr,
                "fixture premise: and clear of the far one, so the first contact is a real sweep");

            var w = new SimulationWorld(7, cfg);
            w.SpawnProjectileForTest(ProjectileOwner.Player, launch,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: pr, ttl: 1f);

            int ricochetsSeen = 0;
            float deepestX = launch.x;
            for (int i = 0; i < 20 && w.ProjectileCount > 0; i++)
            {
                w.Tick(default);
                if (w.ProjectileCount == 0) break;
                ricochetsSeen = math.max(ricochetsSeen, w.Projectiles[0].Ricochets);
                deepestX = math.min(deepestX, w.Projectiles[0].Pos.x);
            }

            Assert.AreEqual(2, ricochetsSeen,
                "счётчик не набрал два отскока на настоящих контактах");
            Assert.AreEqual(0, w.ProjectileCount,
                "третий контакт с исчерпанным счётчиком не погасил снаряд");
            Assert.GreaterOrEqual(deepestX, near.x + rNear,
                "отражённый раунд ушёл сквозь второй барьер");
        }

        [Test]
        public void ExpiredRoundDoesNotLiveOneExtraTickByRicocheting()
        {
            // A GUARD ON TODAY'S CODE, like FloorDoesNotRicochet_Guard above,
            // and for the same reason: today EVERY contact extinguishes the
            // round, so "an expired round does not survive the tick it ricochets
            // on" is already true. It earns its place against the mutant that
            // reflects without testing the lifetime — today `Ttl <= 0` is read
            // in the "nothing was hit" arm alone -- ProjectileSystem.Update's
            // own `default:` case -- and the ricochet arm is a second place a
            // round can leave a tick alive.
            //
            // The lifetime is stated in TICKS off the tick length, so it
            // expires ON the tick the contact falls on and not one either side.
            SimConfig cfg = Fixture();
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float2 from = obstacle - new float2(r + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, from,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Weapon.ProjectileRadius,
                ttl: 2.5f * SimulationWorld.TickDt);

            w.Tick(default);
            w.Tick(default);
            Assert.AreEqual(1, w.ProjectileCount,
                "fixture premise: the round is still alive going into the tick it meets the barrier on");
            Assert.Less(w.Projectiles[0].Pos.x, obstacle.x - r - cfg.Weapon.ProjectileRadius,
                "fixture premise: and has not reached the barrier yet");

            w.Tick(default);

            Assert.AreEqual(0, w.ProjectileCount,
                "снаряд с истёкшим TTL прожил лишний тик, отскочив");
        }

        /// A MOB'S ROUND FLIES BY THE GUNNER ARCHETYPE'S NUMBERS, and until
        /// this test the fork that says so had no witness at all: every round
        /// in this file was `ProjectileOwner.Player`, so
        /// `Impact.RicochetNumbersFor` was only ever asked the question it
        /// answers out of `cfg.Weapon`. A version that answered `cfg.Weapon`
        /// for BOTH owners was indistinguishable from the real one here.
        ///
        /// THE TWO SOURCES ARE DRIVEN APART UNTIL NO VERDICT IS REACHABLE ON
        /// THE WRONG ONE. The weapon is given a counter of ZERO — on the
        /// weapon's numbers this round does not reflect at all, it is
        /// extinguished on contact the way every round was before this task —
        /// while the gunner is given exactly one reflection and a retention of
        /// his own, 0.5 against the weapon's 0.8. So the counter claim cannot
        /// pass on the weapon's numbers (there would be no round left to read)
        /// and neither can the speed claim (a survivor on the weapon's numbers
        /// would be moving at 0.8 of its speed, not 0.5).
        ///
        /// THE OWNER INDEX IS PASSED EXPLICITLY, AND IT HAS TO BE.
        /// `SpawnProjectileForTest` defaults `ownerIndex` to 0 — the solo
        /// player — and does NOT infer `NoOwner` from `ProjectileOwner.Mob`;
        /// its own doc carries that warning in capitals. The fork is keyed on
        /// the INDEX and not on the owner enum, so a Mob-owned round spawned
        /// with the default index would read the weapon's numbers and this test
        /// would be about nothing at all.
        ///
        /// THE ROUND IS THE GUNNER'S IN EVERY NUMBER, not only in its ricochet
        /// three: its speed and its radius are his too. That is the same rule
        /// `Impact.ProjectileMassFor` already states for the mass, and a
        /// fixture that mixed one archetype's round shape with another's
        /// ricochet numbers would leave every claim below ambiguous about which
        /// half it was reading.
        [Test]
        public void MobOwnedRound_ReadsTheGunnerArchetypesRicochetNumbers()
        {
            SimConfig cfg = Fixture();
            cfg.Weapon.MaxRicochets = 0;          // the player's round would be extinguished
            cfg.Gunner.MaxRicochets = 1;          // the gunner's is allowed exactly one
            cfg.Gunner.RicochetRetention = 0.5f;  // and damps by a factor of its own
            cfg.Gunner.RicochetMinSpeed = 6f;
            Assert.AreNotEqual(cfg.Weapon.RicochetRetention, cfg.Gunner.RicochetRetention,
                "fixture premise: the two retentions differ, or the speed claim below cannot "
                + "tell which of the two was read");
            Assert.GreaterOrEqual(cfg.Gunner.ProjectileSpeed * cfg.Gunner.RicochetRetention,
                cfg.Gunner.RicochetMinSpeed,
                "fixture premise: the gunner's own damped speed clears the gunner's own floor, "
                + "so what the counter claim measures is the counter and not the floor");
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            float r = cfg.Arena.ObstacleRadius[0];
            float2 from = obstacle - new float2(r + 3f, 0f);
            w.SpawnProjectileForTest(ProjectileOwner.Mob, from,
                new float2(cfg.Gunner.ProjectileSpeed, 0f), height: 1f, velZ: 0f,
                damage: 1f, radius: cfg.Gunner.ProjectileRadius, ttl: 1f,
                ownerIndex: ProjectileIds.NoOwner);
            // Twelve ticks, not the six the player's fixtures use: the gunner's
            // round travels at 14 m/s against the weapon's 35, so the same 2.85 m
            // gap takes seven ticks instead of three.
            for (int i = 0; i < 12 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++) w.Tick(default);

            Assert.AreEqual(1, w.Projectiles[0].Ricochets,
                "мобий раунд не отскочил — числа рикошета взяты у оружия, где счётчик ноль");
            Assert.AreEqual(cfg.Gunner.ProjectileSpeed * cfg.Gunner.RicochetRetention,
                math.length(new float3(w.Projectiles[0].Vel, w.Projectiles[0].VelZ)), 0.05f,
                "скорость после отскока гашена оружейным Retention, а не мобьим");
        }

        /// THE RING BOUNDARY — THE SECOND OF THE TWO CONTACTS THIS TASK OFFERS
        /// THE RICOCHET AT ALL, and before this test the rim did not appear in
        /// this file once. `ProjectileSystem` resolves the interior barrier and
        /// the arena's rim through ONE shared arm: they end in the same event
        /// and they are handed to the same `TryRicochet`. But they arrive at
        /// that arm as two SEPARATE candidates out of two separate solvers, and
        /// only one of the two had a witness — a version that offered the
        /// ricochet the interior barrier alone passed every other test here.
        ///
        /// AND THE RIM'S NORMAL COMES FROM SOMEWHERE ELSE THAN A BARRIER'S,
        /// which is the second reason this is not a copy of the tests above: an
        /// obstacle's normal rides into the arm on the step itself, the rim's is
        /// re-derived from the contact point by `Geometry.RingWallNormal`. "The
        /// same way it does off a barrier" in the name is the claim that the two
        /// paths end in the same BEHAVIOR, not merely in the same method.
        ///
        /// THREE CLAIMS, AND NOT ONE CLAIM STATED THREE TIMES: the counter says
        /// the contact was offered to the ricochet at all; the direction says
        /// the round was REFLECTED rather than merely left alive; and the
        /// position says it came to rest INSIDE the world it reflected off, which
        /// is what "did not leak through the rim" looks like as a coordinate
        /// rather than as an event.
        ///
        /// THE TICK THAT FOLLOWS THEM MEANS SOMETHING WEAKER HERE THAN AT AN
        /// OBSTACLE, AND SAYS SO. At the obstacle, `RicochetedRound_DoesNot
        /// SinkThroughTheWall` pairs "landed clear" with "still alive a tick
        /// later" because `Geometry.SegmentCircle` has a start-inside branch
        /// that would answer `t = 0` on a landing a ULP too deep and kill the
        /// round on its own touchdown point. The rim's solver has no such
        /// branch — it reports the OUTBOUND crossing and nothing else — so this
        /// tick states that the reflected round keeps flying, which is a real
        /// claim about the reflection, and not that a skin rescued it.
        [Test]
        public void RoundRicochetsOffTheRingBoundary_TheSameWayItDoesOffABarrier()
        {
            SimConfig cfg = RingBoundaryFixture();
            var w = new SimulationWorld(7, cfg);
            float pr = cfg.Weapon.ProjectileRadius;
            // Outward along -X, and the direction is stated rather than left
            // to chance. The lobby's spawn ring puts player 0 on the +X axis,
            // and that body is this round's OWN shooter, which the gather phase
            // skips whichever way the round is fired -- so the choice is belt
            // and braces, not a requirement. It is worth the line anyway: a
            // fixture whose only body stands behind the muzzle cannot be
            // quietly broken by a future narrowing of that skip, and the rim
            // stays the only candidate this test's subject depends on.
            float2 outward = new float2(-1f, 0f);
            const float ttl = 2f;
            Assert.AreEqual(0,
                cfg.Arena.ObstacleCount + cfg.Arena.WallCount + cfg.Arena.ZoneWallCount,
                "fixture premise: no interior geometry at all stands between the muzzle and "
                + "the rim, so the rim is the candidate the min-scan has to pick");
            Assert.Less(math.dot(w.PlayerAt(0).Pos, outward), 0f,
                "fixture premise: the lobby's only body stands BEHIND the muzzle, so the rim "
                + "stays the round's first contact even without the gather phase's owner skip");
            Assert.Greater(ttl, (cfg.Arena.Radius - pr) / cfg.Weapon.ProjectileSpeed,
                "fixture premise: the round outlives the flight to the rim, so what the "
                + "counter claim measures is the ricochet and not the lifetime");
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                outward * cfg.Weapon.ProjectileSpeed, height: 1f, velZ: 0f,
                damage: 1f, radius: pr, ttl: ttl);
            // Forty ticks against the eighteen the flight costs: the bound is a
            // safety margin on the loop, never the thing under test.
            for (int i = 0; i < 40 && w.ProjectileCount > 0
                 && w.Projectiles[0].Ricochets == 0; i++) w.Tick(default);

            Assert.AreEqual(1, w.Projectiles[0].Ricochets,
                "раунд не отскочил от обода — контакт обода рикошету не предложен");
            Assert.Less(math.dot(w.Projectiles[0].Vel, math.normalize(w.Projectiles[0].Pos)), 0f,
                "отражённый раунд не развернулся внутрь арены");
            Assert.LessOrEqual(math.length(w.Projectiles[0].Pos), cfg.Arena.Radius,
                "отражённый раунд сел ЗА ободом — позиция взята с конца шага, а не с контакта");

            w.Tick(default);

            Assert.AreEqual(1, w.ProjectileCount,
                "отражённый от обода раунд не пережил следующий тик");
        }
    }
}
