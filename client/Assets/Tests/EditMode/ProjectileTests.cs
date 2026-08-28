using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileTests
    {
        static readonly SimInput FireRight = new SimInput
            { AimPoint = new float2(20f, 0f), FireHeld = true };

        static SimConfig NoSpread()
        {
            var c = TestConfigs.OpenField();
            c.Weapon.SpreadRad = 0f; c.Weapon.RecoilPerShotRad = 0f;
            // app-88jb Т19 (coordinator Ruling 94): ObstacleBeforeMob_BlocksShot
            // _NoDamage below is about a barrier SCREENING a mob — it waits for
            // a ProjectileBlocked and for ShotsHit to stay 0. At the baseline's
            // own MaxRicochets (ONE — the fixture value spec §4.3 assigns,
            // against the game's 2) the round reflects off the obstacle instead,
            // flies back down the +X axis it came from, and expires in open
            // field 173 m from the rim (TestConfigs.DefaultArena's Radius,
            // against a 52.5 m reach): no ProjectileBlocked is ever emitted and
            // the wait fails. Stated here, beside the other two numbers this
            // fixture already states about itself.
            //
            // ⚠ ONLY THE WEAPON'S NUMBER, AND THE MOB'S IS LEFT ALONE ON
            // PURPOSE (coordinator Ruling 99). A mob-owned round reads the
            // Gunner archetype's own MaxRicochets (Impact.RicochetNumbersFor),
            // so this fixture would owe that number too IF a mob-owned round
            // could fly here. None can: this is TestConfigs.OpenField(), which
            // is Open() off Quiet(), and Quiet's whole job is
            // `Wave.FirstWaveDelay = 1e6f` — there are no waves at all, so there
            // is no gunner to fire one. A second zero would be a line with no
            // reader.
            // THE RULE: a fixture here that spawns a LIVE gunner must state its
            // own Gunner.MaxRicochets.
            c.Weapon.MaxRicochets = 0;
            return c;
        }

        [Test]
        public void PlayerShot_KillsMob_EmitsHitAndDeath()
        {
            var w = new SimulationWorld(1, NoSpread());
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            int hits = 0, deaths = 0;
            for (int i = 0; i < 60 && deaths == 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                {
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileHit) hits++;
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) deaths++;
                }
            }
            Assert.Greater(hits, 0);
            Assert.AreEqual(1, deaths);
            Assert.AreEqual(1, w.Stats.Kills);
        }

        [Test]
        public void FastProjectile_SmallTarget_NoTunnel()
        {
            var c = NoSpread();
            c.Weapon.ProjectileSpeed = 120f; // 4 m/tick >> target diameter of 1 m
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            for (int i = 0; i < 30; i++) w.Tick(FireRight);
            Assert.Greater(w.Stats.ShotsHit, 0);
        }

        [Test]
        public void ObstacleBeforeMob_BlocksShot_NoDamage()
        {
            var c = NoSpread();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(9f, 0f));
            bool blocked = false;
            for (int i = 0; i < 60; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileBlocked) blocked = true;
            }
            Assert.IsTrue(blocked);
            Assert.AreEqual(0, w.Stats.ShotsHit);
        }

        [Test]
        public void TwoTargetsOnPath_NearestDiesFirst()
        {
            var w = new SimulationWorld(1, NoSpread());
            int nearId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            int firstDeadId = -1;
            for (int i = 0; i < 90 && firstDeadId < 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied)
                    { firstDeadId = w.GetEvent(e).EntityId; break; }
            }
            Assert.AreEqual(nearId, firstDeadId); // the nearer target dies first
        }

        [Test]
        public void MobShot_IframesAbsorb_ThenDamagePasses()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            // enemy projectile right in front of the player during the dash frame — i-frames are active
            // Stage 2 Task 10 (carryover-t10.md item 2): explicit NoOwner — the
            // seam's default 0 models a solo PLAYER's shot, and OwnerIndex is
            // part of StateHash from this task on.
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 1f, 0f, 8f, 0.15f, 3f,
                ownerIndex: ProjectileIds.NoOwner);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            w.Tick(default);
            Assert.AreEqual(c.Hero.MaxHp, w.Player.Hp); // i-frames absorbed it
            for (int i = 0; i < 10; i++) w.Tick(default); // dash and i-frames have expired
            // second projectile — from the player's CURRENT position (shifted after the dash)
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 1f, 0f, 8f, 0.15f, 3f,
                ownerIndex: ProjectileIds.NoOwner); // Stage 2 Task 10: see above
            for (int i = 0; i < 4; i++) w.Tick(default);
            Assert.Less(w.Player.Hp, c.Hero.MaxHp);
        }

        [Test]
        public void MultiKillSameTick_SwapRemoveKeepsListConsistent() // spec §3.13 item 11
        {
            var w = new SimulationWorld(1, NoSpread());
            int a = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            int b = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, 0.4f));
            int c = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, -0.4f));
            Assert.IsTrue(a != b && b != c); // ids are stable and unique
            // three wide projectiles wipe everyone out in one tick — swap-remove mid-list
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0.4f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, -0.4f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            int died = 0;
            for (int i = 0; i < 5; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) died++;
            }
            Assert.AreEqual(3, died); // nobody lost, nobody double-counted
            var snap = new RenderSnapshot(NoSpread());
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
        }

        [Test]
        public void ShippedNumbers_PlayerShotDoesNotPierce() // §3.5 negative case
        {
            // Stage 3 Task 5 (spec Р252): this test used to be named
            // DamageMatrix_MobShotIgnoresMobs_PlayerShotNoPiercing and also
            // covered "a mob's round ignores mobs" — that premise is gone now
            // that a mob-owned round hits every OTHER live mob by design
            // (MobFriendlyFireTests.GunnerRound_DamagesAnotherMob covers the
            // positive case, MobRound_DoesNotDamageItsOwnShooter the one
            // exclusion — CR 2, no point duplicating that coverage here). What
            // survives is the "no piercing" half — and app-88jb Т20 turned that
            // from a RULE into a fact about the SHIPPED NUMBERS, which is why
            // this test was renamed with it. A player's round now pierces a
            // body it strictly overkills and is heavy enough for; at 2.6
            // against a chaser's 90 kg the ratio is 0.029 against a threshold
            // of 0.06, so it pierces nobody, and that is what this measures.
            // The finer-grained sibling is
            // ProjectileFlightTests.ShippedNumbers_PierceNobody_ObservedThroughTheWorld;
            // this one is kept because it also pins the far body's exact Hp on
            // a different fixture.
            var cfg = NoSpread();
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            // at the shipped numbers an overkill player round only kills the nearest
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 1f, 0f, 1000f, 0.12f, 1f);
            for (int i = 0; i < 6; i++) w.Tick(default);
            var snap = new RenderSnapshot(cfg);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(1, snap.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, snap.Mobs[0].Hp); // the far one is alive and unscathed
        }

        [Test]
        public void MobProjectile_HasNoOwnerIndex()
        {
            // Stage 2 Task 7: a Mob-owned projectile never has a shooter —
            // MobAiSystem's SpawnProjectile call passes ProjectileIds.NoOwner
            // explicitly (task-7-context.md §2.2), so ProjectileState.OwnerIndex
            // reads NoOwner, not a stale/hardcoded player index. Spawns through
            // the real production path (MobAiSystem, a live Gunner) rather than
            // SpawnProjectileForTest, mirroring EventTests.
            // ProjectileFired_CarriesOwner_PlayerAndMob's own "pin the actual
            // call site" rationale — and reuses that same test's proven Gunner
            // fixture position (well inside PreferredRange+-RangeTolerance with
            // clear LoS, fires on its first eligible tick).
            var c = TestConfigs.OpenField();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f));
            bool fired = false;
            for (int i = 0; i < 60 && !fired; i++)
            {
                w.Tick(default);
                fired = w.ProjectileCount > 0;
            }
            Assert.IsTrue(fired, "Gunner never fired within the tick budget");
            Assert.AreEqual(ProjectileIds.NoOwner, w.GetProjectileForTest(0).OwnerIndex);
        }

        [Test]
        public void SpawnProjectileForTest_OmittedOwnerIndex_DefaultsToSoloPlayer()
        {
            // Stage 2 Task 7 (task-7-context.md decision 3): omitting ownerIndex
            // must default to 0 — Э1's dozens of existing SpawnProjectileForTest
            // call sites model a solo player's own shot and assert its credit; a
            // NoOwner default would silently rob them of it.
            var w = new SimulationWorld(1, NoSpread());
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(1f, 0f), new float2(1f, 0f),
                1f, 0f, 10f, 0.1f, 1f);
            Assert.AreEqual(0, w.GetProjectileForTest(0).OwnerIndex);

            // Fix-round 1 M-3: the assertion above is satisfied by BOTH a correct
            // implementation AND a broken one that never forwards ownerIndex into
            // SpawnProjectile at all (0 == default(byte) either way). This second
            // pair — an EXPLICIT non-zero ownerIndex — only passes if the value
            // actually threads through; together the two pin both the default and
            // the forwarding.
            var w2 = new SimulationWorld(1, NoSpread());
            w2.SpawnProjectileForTest(ProjectileOwner.Player, new float2(1f, 0f), new float2(1f, 0f),
                1f, 0f, 10f, 0.1f, 1f, ownerIndex: 1);
            Assert.AreEqual(1, w2.GetProjectileForTest(0).OwnerIndex);
        }

        [Test]
        public void ProjectileOwner_CreditsShooterStats_NotAlwaysPlayerZero()
        {
            // Stage 2 Task 7 (carryover I-2 from the T5 review, carryover-t7.md):
            // DamageMob used to hardcode Increment*(0) — a hit from ANY player's
            // projectile always credited player 0's personal stats. Now it must
            // route to the projectile's OWN OwnerIndex, so player 1's kill lands
            // on player 1's stats, not player 0's — the exact "10 own shots + 40
            // others' hits = 500% accuracy" bug the carryover describes.
            var w = new SimulationWorld(1, NoSpread(), playerCount: 2);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0f),
                new float2(35f, 0f), 1f, 0f, 1000f, 0.6f, 1f, ownerIndex: 1); // player 1 fired it
            var inputs = new SimInput[2];
            for (int i = 0; i < 6; i++) w.TickAll(inputs);
            Assert.AreEqual(1, w.StatsAt(1).Kills, "player 1 fired — the kill must land on their own stats");
            Assert.AreEqual(0, w.StatsAt(0).Kills, "player 0 never fired — their stats must stay untouched");
        }

        [Test]
        public void Ttl_ExpiresWithEvent()
        {
            var c = NoSpread();
            c.Weapon.ProjectileLifetime = 0.1f; // 3 ticks
            var w = new SimulationWorld(1, c);
            w.Tick(FireRight);
            bool expired = false;
            for (int i = 0; i < 6; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileExpired) expired = true;
            }
            Assert.IsTrue(expired);
        }
    }
}
