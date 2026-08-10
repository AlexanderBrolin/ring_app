using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 17: the PvP half of the damage matrix and the attacker
    /// index that rides with it. A Player-owned round now gathers every OTHER
    /// live player (never its own owner) alongside the mobs, a Mob-owned round
    /// gathers every live player instead of a hardcoded player 0, the blow
    /// lands on the victim the geometry actually picked, and
    /// ShotsHit/Kills/HeadshotKills go to the ATTACKER — with a mob-owned blow
    /// (attacker ProjectileIds.NoOwner) crediting nobody at all.
    public class PvpDamageTests
    {
        /// Open arena, no spread/recoil, immobile mobs: every fixture below
        /// places bodies by hand and measures which one a single round reaches,
        /// so a wandering target or a randomised muzzle angle would move the
        /// contact point the expectations are built from. Same shape (and same
        /// reasons) as HitZoneTests.Range().
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

        /// Point-blank duel distance: 1 m is under one tick of projectile
        /// travel (Weapon.ProjectileSpeed / 30), so a round fired here lands on
        /// the very next tick and no fixture has to budget for flight time.
        const float TargetX = 1f;

        /// Shooter (player 0) at the origin, victim (player 1) TargetX metres
        /// down the +X axis.
        static SimulationWorld Duel(out SimConfig c)
        {
            c = Range();
            Assert.Less(TargetX, c.Weapon.ProjectileSpeed * SimulationWorld.TickDt,
                "fixture premise: the victim stands within one tick of projectile travel, so "
                + "every duel fixture lands its round on the very next tick");
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(TargetX, 0f));

            // Same depenetration discipline TestWorlds.TrioSaturated carries
            // (Ф4 fix-wave): every expectation below is built from these two
            // marks, so the fixture states outright that the bodies are ON
            // them. Geometry.Depenetrate runs unconditionally every tick
            // (PlayerMovementSystem) and pushes a body out of arena geometry —
            // Range() inherits Open(), which has none (asserted), and bodies do
            // not depenetrate against each other at all, so here the check is
            // cheap where the trio's is load-bearing. One phase, one discipline.
            Assert.AreEqual(0, c.Arena.ObstacleCount + c.Arena.WallCount,
                "fixture premise: the duel arena carries no geometry to depenetrate against");
            const float posTolerance = 1e-4f;
            Assert.Less(math.distance(w.PlayerAt(0).Pos, float2.zero), posTolerance,
                "fixture premise: the shooter stands exactly at the origin");
            Assert.Less(math.distance(w.PlayerAt(1).Pos, new float2(TargetX, 0f)), posTolerance,
                "fixture premise: the victim stands exactly TargetX downrange");
            return w;
        }

        /// Forces a player mid-slide (QA1 seam, same as HitZoneTests) — no need
        /// to choreograph a real run-up just to get SlideTimer > 0. SlideDir is
        /// left at zero on purpose: the slide tick multiplies it into Vel, so
        /// the body keeps its placed position for the whole fixture.
        static void Slide(SimulationWorld w, int index, in SimConfig c)
        {
            PlayerState p = w.PlayerAt(index);
            p.SlideTimer = c.Hero.SlideDuration;
            w.SetPlayerForTest(index, p);
        }

        static void SetIframes(SimulationWorld w, int index, float seconds)
        {
            PlayerState p = w.PlayerAt(index);
            p.IframeTimer = seconds;
            w.SetPlayerForTest(index, p);
        }

        static void TickIdle(SimulationWorld w, int ticks = 1)
        {
            var inputs = new SimInput[w.PlayerCount];
            for (int i = 0; i < ticks; i++) w.TickAll(inputs);
        }

        /// Mid-band heights of the hero's own zone table — expectations are
        /// fixture EXPRESSIONS, never numbers restated from the balance data.
        static float BodyBand(in SimConfig c) => 0.5f * (c.Hero.LegsTop + c.Hero.BodyTop);
        static float HeadBand(in SimConfig c) => 0.5f * (c.Hero.BodyTop + c.Hero.HeadTop);

        [Test]
        public void PlayerShot_DamagesOtherPlayer()
        {
            SimulationWorld w = Duel(out SimConfig c);
            TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c), new float2(TargetX, 0f), BodyBand(c),
                ownerIndex: 0);

            w.ClearEvents();
            TickIdle(w);

            float expected = c.Weapon.Damage * c.Hero.BodyDamageMult;
            Assert.AreEqual(c.Hero.MaxHp - expected, w.PlayerAt(1).Hp, 1e-4f,
                "player 0's round must take the hit zone's damage off player 1");
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-4f, "the shooter must be untouched");
            Assert.AreEqual(expected, w.StatsAt(1).DamageTaken, 1e-4f,
                "DamageTaken belongs to the victim");
            Assert.AreEqual(1, w.StatsAt(0).ShotsHit, "the landed hit belongs to the shooter");
            Assert.AreEqual(0, w.StatsAt(1).ShotsHit, "the victim never fired");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(1, damaged.PlayerIndex, "PlayerDamaged carries the VICTIM's index");
        }

        /// Same round, same line, same muzzle — only the owner index differs.
        static SimulationWorld RoundThroughPlayerZero(byte ownerIndex, out SimConfig c)
        {
            c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 20f)); // well clear of the firing line
            TestWorlds.FireAimed3D(w, new float2(-2f, 0f), BodyBand(c), float2.zero, BodyBand(c),
                ownerIndex);
            w.ClearEvents();
            TickIdle(w, 4); // 4 ticks of travel carry the round clean past the origin
            return w;
        }

        [Test]
        public void PlayerShot_DoesNotDamageOwner()
        {
            SimulationWorld own = RoundThroughPlayerZero(0, out SimConfig c);
            Assert.AreEqual(c.Hero.MaxHp, own.PlayerAt(0).Hp, 1e-4f,
                "a player's own round must never gather them as a target");
            Assert.AreEqual(0, TestEvents.CountOf(own, SimEventKind.PlayerDamaged));
            Assert.AreEqual(0, own.StatsAt(0).ShotsHit, "no hit means no credit");

            // Control: the IDENTICAL round owned by player 1 does land on
            // player 0 — so it is the owner check, not the geometry, that
            // spared the shooter above.
            SimulationWorld foreign = RoundThroughPlayerZero(1, out _);
            Assert.Less(foreign.PlayerAt(0).Hp, c.Hero.MaxHp,
                "another player's round on the same line must connect");
            Assert.AreEqual(1, foreign.StatsAt(1).ShotsHit, "credit follows the round's owner");
            Assert.AreEqual(0, foreign.StatsAt(0).ShotsHit, "the victim is not credited with being hit");
        }

        [Test]
        public void HeadZoneOnPlayer_AppliesMultiplier()
        {
            SimulationWorld w = Duel(out SimConfig c);
            Assert.AreNotEqual(1f, c.Hero.HeadDamageMult,
                "fixture premise: the hero's head multiplier must not be neutral");
            TestWorlds.FireAimed3D(w, float2.zero, HeadBand(c), new float2(TargetX, 0f), HeadBand(c),
                ownerIndex: 0);

            w.ClearEvents();
            TickIdle(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(HitZone.Head, damaged.Zone,
                "a round on the hero's own head band reads Head");
            float expected = c.Weapon.Damage * c.Hero.HeadDamageMult;
            Assert.AreNotEqual(c.Weapon.Damage, damaged.Amount, "NOT the round's base damage");
            Assert.AreEqual(expected, damaged.Amount, 1e-4f, "Amount is the POST-multiplier damage");
            Assert.AreEqual(c.Hero.MaxHp - expected, w.PlayerAt(1).Hp, 1e-4f);
        }

        [Test]
        public void SlidingTarget_IsMissedByHorizontalShot()
        {
            SimulationWorld sliding = Duel(out SimConfig c);
            Assert.Less(c.Hero.SlideProfileTop + c.Weapon.ProjectileRadius, c.Hero.MuzzleHeight,
                "fixture premise: a round on the muzzle line must clear the sliding profile");
            Slide(sliding, 1, in c);
            TestWorlds.FireAimed3D(sliding, float2.zero, c.Hero.MuzzleHeight,
                new float2(TargetX, 0f), c.Hero.MuzzleHeight, ownerIndex: 0);

            sliding.ClearEvents();
            TickIdle(sliding);

            Assert.AreEqual(c.Hero.MaxHp, sliding.PlayerAt(1).Hp, 1e-4f,
                "the sliding profile must have let the round pass clean over");
            Assert.AreEqual(0, TestEvents.CountOf(sliding, SimEventKind.PlayerDamaged));

            // Control: player 0 (the SHOOTER) is not sliding in either world,
            // so the profile that saved player 1 above can only have come from
            // player 1's own state — against a standing player 1 the same
            // round connects.
            SimulationWorld standing = Duel(out _);
            TestWorlds.FireAimed3D(standing, float2.zero, c.Hero.MuzzleHeight,
                new float2(TargetX, 0f), c.Hero.MuzzleHeight, ownerIndex: 0);
            standing.ClearEvents();
            TickIdle(standing);

            Assert.Less(standing.PlayerAt(1).Hp, c.Hero.MaxHp,
                "a standing target on the same line must be hit");
        }

        [Test]
        public void IframesAbsorbPvpDamage()
        {
            SimulationWorld w = Duel(out SimConfig c);
            SetIframes(w, 1, c.Hero.DashIframes);
            TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c), new float2(TargetX, 0f), BodyBand(c),
                ownerIndex: 0);

            w.ClearEvents();
            TickIdle(w);

            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(1).Hp, 1e-4f, "dash i-frames absorb a PvP round");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.PlayerDamaged));
            Assert.AreEqual(0, w.StatsAt(0).ShotsHit,
                "an absorbed round dealt no damage — DamagePlayer returns before any credit, " +
                "exactly as it does for an already-dead victim");
            Assert.AreEqual(0, w.ProjectileCount, "the round is still consumed on contact");

            // Control: with the i-frames gone the identical round lands, so the
            // absorption above is the i-frame guard and not a dead PvP path.
            SetIframes(w, 1, 0f);
            TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c), new float2(TargetX, 0f), BodyBand(c),
                ownerIndex: 0);
            TickIdle(w);

            Assert.Less(w.PlayerAt(1).Hp, c.Hero.MaxHp, "past the i-frames the round must land");
            Assert.AreEqual(1, w.StatsAt(0).ShotsHit, "and only THAT round is credited");
        }

        // ---- Stage 2 Task 44a: the PvP hit's own event -------------------

        /// Slot of the shooter and of the victim in the fixture below. THREE
        /// slots, and the victim in the LAST one, for a reason the tests
        /// depend on: SimulationWorld hands out entity ids from 1, so in a
        /// two-player duel the fixture's first round is id 1 and the victim is
        /// index 1 — the two numbers the event must keep apart would be equal,
        /// and an emit that wrote the victim into SecondaryEntityId (or the
        /// round into EntityId) would pass unnoticed. The shooter's own slot is
        /// likewise never the victim's, so a swap of EntityId and PlayerIndex
        /// cannot pass either.
        const int ShooterSlot = 0, VictimSlot = 2, BystanderSlot = 1;

        /// Duel() geometry with a third body: shooter at the origin, victim
        /// TargetX downrange, bystander well off the firing line so it can
        /// never intercept the round (asserted, not assumed).
        static SimulationWorld TrioDuel(out SimConfig c)
        {
            c = Range();
            Assert.Less(TargetX, c.Weapon.ProjectileSpeed * SimulationWorld.TickDt,
                "fixture premise: the victim stands within one tick of projectile travel");
            Assert.AreEqual(0, c.Arena.ObstacleCount + c.Arena.WallCount,
                "fixture premise: the duel arena carries no geometry to depenetrate against");

            var w = new SimulationWorld(1, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, ShooterSlot, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, VictimSlot, new float2(TargetX, 0f));
            const float bystanderOffAxis = 10f;
            TestWorlds.RelocatePlayerForTest(w, BystanderSlot, new float2(0f, -bystanderOffAxis));

            const float posTolerance = 1e-4f;
            Assert.Less(math.distance(w.PlayerAt(ShooterSlot).Pos, float2.zero), posTolerance,
                "fixture premise: the shooter stands exactly at the origin");
            Assert.Less(math.distance(w.PlayerAt(VictimSlot).Pos, new float2(TargetX, 0f)), posTolerance,
                "fixture premise: the victim stands exactly TargetX downrange");
            Assert.Greater(bystanderOffAxis, c.Hero.Radius + c.Weapon.ProjectileRadius,
                "fixture premise: the bystander is clear of the firing line, so every hit below is "
                + "on the victim the expectations name");
            return w;
        }

        /// The one buffered ProjectileHitPlayer, or a failed assertion saying
        /// so — every test below opens with this, and none of them should be
        /// able to read a second event's fields by accident.
        static SimEvent SolePvpHit(SimulationWorld w)
        {
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileHitPlayer),
                "exactly one round ended on a player this tick");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHitPlayer, out SimEvent hit));
            return hit;
        }

        [Test]
        public void PvpHit_EmitsItsOwnKind_VictimInEntityId_ShooterInPlayerIndex()
        {
            SimulationWorld w = TrioDuel(out SimConfig c);
            TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c), new float2(TargetX, 0f), BodyBand(c),
                ownerIndex: (byte)ShooterSlot);

            w.ClearEvents();
            TickIdle(w);

            Assert.AreNotEqual(ShooterSlot, VictimSlot,
                "fixture premise: the two indices this test compares must be different numbers, "
                + "or a swapped EntityId/PlayerIndex would pass");
            SimEvent hit = SolePvpHit(w);
            Assert.AreEqual(VictimSlot, hit.EntityId,
                "EntityId is the VICTIM's player slot, mirroring ProjectileHit's victim convention");
            Assert.AreEqual(ShooterSlot, hit.PlayerIndex,
                "PlayerIndex is the SHOOTER — the whole point of the kind (app-dsh): a player must be "
                + "able to tell its own PvP hit from somebody else's");

            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.ProjectileHit),
                "a player victim must never ride the mob-hit kind — the snapshot assembler maps that "
                + "one to a hardcoded ProjectileEndKind.HitMob");
            Assert.AreEqual(c.Weapon.Damage * c.Hero.BodyDamageMult, hit.Amount, 1e-4f,
                "Amount is the post-multiplier damage, same contract as ProjectileHit");
            Assert.AreEqual(1f, hit.HitDir.x, 1e-3f,
                "HitDir is the round's travel direction at contact — this shot flies straight down +X");
            Assert.AreEqual(0f, hit.HitDir.y, 1e-3f);
            Assert.Greater(hit.Pos.x, 0f,
                "Pos is the CONTACT point, somewhere between the muzzle and the victim's centre");
            Assert.LessOrEqual(hit.Pos.x, TargetX + 1e-3f);
        }

        [Test]
        public void PvpHit_CarriesTheRoundIdInSecondaryEntityId_NotInEntityId()
        {
            SimulationWorld w = TrioDuel(out SimConfig c);
            int roundId = TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c),
                new float2(TargetX, 0f), BodyBand(c), ownerIndex: (byte)ShooterSlot);

            w.ClearEvents();
            TickIdle(w);

            Assert.AreNotEqual(roundId, VictimSlot,
                "fixture premise: the round's id and the victim's slot must be different numbers, or "
                + "the two assertions below could not tell the fields apart");
            SimEvent hit = SolePvpHit(w);
            Assert.AreEqual(roundId, hit.SecondaryEntityId,
                "the round's own identity survives the emit — without it the snapshot assembler cannot "
                + "close the per-connection spawn subscription this hit ends");
            Assert.AreNotEqual(hit.EntityId, hit.SecondaryEntityId,
                "witness: the two id slots carry DIFFERENT things — victim and round, not one value twice");
        }

        [Test]
        public void PvpHit_EmittedEvenWhenIframesAbsorbTheDamage()
        {
            SimulationWorld w = TrioDuel(out SimConfig c);
            SetIframes(w, VictimSlot, c.Hero.DashIframes);
            int roundId = TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c),
                new float2(TargetX, 0f), BodyBand(c), ownerIndex: (byte)ShooterSlot);

            w.ClearEvents();
            TickIdle(w);

            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(VictimSlot).Hp, 1e-4f,
                "fixture premise: the i-frames really did absorb the blow");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.PlayerDamaged),
                "fixture premise: an absorbed blow emits no PlayerDamaged");
            Assert.AreEqual(0, w.ProjectileCount,
                "fixture premise: the round is consumed on contact all the same");

            SimEvent hit = SolePvpHit(w);
            Assert.AreEqual(roundId, hit.SecondaryEntityId,
                "the event reports the ROUND ENDING, not the damage landing — a tracer whose end went "
                + "unreported would hang on the client until its confirm timeout");
            Assert.AreEqual(c.Weapon.Damage * c.Hero.BodyDamageMult, hit.Amount, 1e-4f,
                "and Amount is what the round CARRIED: DamagePlayer returns nothing, so the applied "
                + "figure is not available at the emit site at all");
        }

        [Test]
        public void PvpHit_ReportsTheZoneTheRoundLandedIn()
        {
            SimulationWorld head = TrioDuel(out SimConfig hc);
            TestWorlds.FireAimed3D(head, float2.zero, HeadBand(hc), new float2(TargetX, 0f), HeadBand(hc),
                ownerIndex: (byte)ShooterSlot);
            head.ClearEvents();
            TickIdle(head);
            Assert.AreEqual(HitZone.Head, SolePvpHit(head).Zone,
                "a headshot must stay distinguishable — ADR-001 §10's per-hit feedback and the head "
                + "hitstop scale both key off this");

            // Positive witness on the same fixture, one band lower: the zone
            // genuinely varies with the shot rather than being a constant that
            // happens to read Head.
            SimulationWorld body = TrioDuel(out SimConfig bc);
            TestWorlds.FireAimed3D(body, float2.zero, BodyBand(bc), new float2(TargetX, 0f), BodyBand(bc),
                ownerIndex: (byte)ShooterSlot);
            body.ClearEvents();
            TickIdle(body);
            SimEvent bodyHit = SolePvpHit(body);
            Assert.AreEqual(HitZone.Body, bodyHit.Zone, "a body shot reports its own band");
            Assert.AreNotEqual(HitZone.Head, bodyHit.Zone);
            Assert.AreNotEqual(HitZone.None, bodyHit.Zone,
                "and never the neutral 'no blow' value — that is what a dropped zone would look like");
        }

        [Test]
        public void KillCreditGoesToShooter()
        {
            var c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);          // victim — the pre-Task-17 hardcoded index
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 20f));  // shooter, well clear of its own firing line

            // Overkill on the head band: the hit, the kill and the headshot kill
            // must all land on player 1's counters and none of them on the
            // victim's.
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(-2f, 0f),
                new float2(c.Weapon.ProjectileSpeed, 0f), HeadBand(c), 0f,
                c.Hero.MaxHp + 1f, c.Weapon.ProjectileRadius, c.Weapon.ProjectileLifetime,
                ownerIndex: 1);

            w.ClearEvents();
            TickIdle(w, 4);

            Assert.IsFalse(w.PlayerAt(0).Alive, "an overkill headshot must kill the victim");
            Assert.AreEqual(1, w.StatsAt(1).Kills, "the kill belongs to the shooter");
            Assert.AreEqual(1, w.StatsAt(1).HeadshotKills, "so does the headshot kill");
            Assert.AreEqual(1, w.StatsAt(1).ShotsHit);
            Assert.AreEqual(0, w.StatsAt(0).Kills, "the victim is not credited with their own death");
            Assert.AreEqual(0, w.StatsAt(0).HeadshotKills);
            Assert.AreEqual(0, w.StatsAt(0).ShotsHit);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDied, out SimEvent died));
            Assert.AreEqual(0, died.PlayerIndex, "PlayerDied carries the VICTIM's index");
        }

        [Test]
        public void MobShot_ReachesAnyLivePlayer_AndCreditsNobody()
        {
            var c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(5f, 0f));

            // A mob-owned round flying -X from beyond player 1 must stop on the
            // first live player it reaches. Before Task 17 the gather packed
            // player 0 and nobody else, so this round would have sailed through
            // player 1 untouched.
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(7f, 0f),
                new float2(-c.Gunner.ProjectileSpeed, 0f), BodyBand(c), 0f,
                c.Gunner.ProjectileDamage, c.Gunner.ProjectileRadius, c.Gunner.ProjectileLifetime,
                ownerIndex: ProjectileIds.NoOwner);

            w.ClearEvents();
            TickIdle(w, 5);

            Assert.Less(w.PlayerAt(1).Hp, c.Hero.MaxHp,
                "a mob's round damages whichever live player it actually reaches");
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-4f,
                "player 0 is 5 m further down the line — this round never got there");
            Assert.AreEqual(0, w.StatsAt(0).ShotsHit,
                "a mob owns no player slot, so the NoOwner guard credits nobody");
            Assert.AreEqual(0, w.StatsAt(1).ShotsHit);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(1, damaged.PlayerIndex);
        }

        [Test]
        public void ChaserStrike_LandsOnTheTargetItChose()
        {
            // carryover-t17.md item 1 (from the Task 8 review): mobs have picked
            // the NEAREST live player since Task 8, but the contact strike still
            // paid out to player 0 — a chaser standing on player 1 hit someone
            // 40 m away. The victim index closes that loop.
            var c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(-30f, 0f)); // far away — the old hardcoded victim
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(10f, 0f));  // the chaser's actual nearest target
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(10f + 0.9f, 0f)));
            Assert.Less(0.9f, c.Chaser.AttackRange, "fixture premise: the chaser starts in range");

            // Idle -> Chase -> Telegraph -> strike after TelegraphSeconds.
            int budget = 4 + (int)math.ceil(c.Chaser.TelegraphSeconds / SimulationWorld.TickDt);
            var inputs = new SimInput[2];
            SimEvent damaged = default;
            bool struck = false;
            for (int i = 0; i < budget && !struck; i++)
            {
                w.ClearEvents();
                w.TickAll(inputs);
                struck = TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out damaged);
            }

            Assert.IsTrue(struck, "the chaser never landed its telegraphed strike");
            Assert.AreEqual(1, damaged.PlayerIndex, "the mob strikes the target it chose");
            Assert.AreEqual(c.Chaser.ContactDamage, w.StatsAt(1).DamageTaken, 1e-4f);
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-4f,
                "a fist 40 m away must not land on player 0");
            Assert.AreEqual(0f, w.StatsAt(0).DamageTaken, 1e-4f);
        }

        [Test]
        public void PlungingShot_HeightGateReadsTheVictimsOwnColumn()
        {
            // The height gate resolves the zone from the chord through the
            // TARGET's own circle. Player 0 stands at the muzzle here, so a gate
            // still reading player 0's position would clip the chord to the
            // round's first few centimetres and report the LAUNCH height (Head,
            // above the crown) instead of the height the round actually arrives
            // at 1.5 m downrange (Legs).
            var c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(1.5f, 0f));

            const float launchHeight = 2f;  // clears the crown at t = 0
            const float plungeVelZ = -60f;  // drops the full launch height across this one step
            Assert.Greater(launchHeight, c.Hero.HeadTop,
                "fixture premise: the round launches above the hero's crown");
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(c.Weapon.ProjectileSpeed, 0f), launchHeight, plungeVelZ,
                c.Weapon.Damage, c.Weapon.ProjectileRadius, c.Weapon.ProjectileLifetime,
                ownerIndex: 0);

            w.ClearEvents();
            TickIdle(w);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(HitZone.Legs, damaged.Zone,
                "the round arrives at the victim's shins, whatever height it launched at");
            float expected = c.Weapon.Damage * c.Hero.LegsDamageMult;
            Assert.AreEqual(expected, damaged.Amount, 1e-4f);
            Assert.AreEqual(c.Hero.MaxHp - expected, w.PlayerAt(1).Hp, 1e-4f);
        }

        [Test]
        public void BarrierOutranksPlayerOnAnExactTie()
        {
            // Canonical packing order (ProjectileSystem's gather): barrier
            // first, then mobs, then players, then floor — and the min-scan
            // breaks ties with a strict `<`, so the lowest-packed candidate wins
            // an exact one. Task 17 had to slot the new per-player loop into the
            // position the single hardcoded player candidate used to hold,
            // BETWEEN the mobs and the floor, because packing it earlier would
            // silently re-order those ties and with them the pinned scenarios.
            // Nothing else pins that placement, so this does: a round spawned
            // inside BOTH an obstacle and a player produces two candidates at
            // t = 0 (Geometry.SegmentCircle's start-inside branch answers 0 for
            // each), and the barrier has to win.
            var c = Range();
            const float obstacleX = 5f, obstacleRadius = 1.5f;
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(obstacleX, 0f) };
            c.Arena.ObstacleRadius = new[] { obstacleRadius };

            // Player 1 stands just clear of the obstacle (no overlap, so nothing
            // depenetrates them off their mark), close enough that the two
            // padded circles still share a sliver of the +X axis.
            float playerX = obstacleX + obstacleRadius + c.Hero.Radius + 0.1f;
            // Muzzle at the midpoint of that sliver — inside both.
            float muzzleX = 0.5f * ((obstacleX + obstacleRadius + c.Weapon.ProjectileRadius)
                + (playerX - c.Hero.Radius - c.Weapon.ProjectileRadius));

            SimulationWorld Fire(bool withObstacle)
            {
                SimConfig cfg = c;
                if (!withObstacle)
                {
                    cfg.Arena.ObstacleCount = 0;
                    cfg.Arena.ObstaclePos = System.Array.Empty<float2>();
                    cfg.Arena.ObstacleRadius = System.Array.Empty<float>();
                }
                var world = new SimulationWorld(1, cfg, playerCount: 2);
                TestWorlds.RelocatePlayerForTest(world, 0, float2.zero);
                TestWorlds.RelocatePlayerForTest(world, 1, new float2(playerX, 0f));
                world.SpawnProjectileForTest(ProjectileOwner.Player, new float2(muzzleX, 0f),
                    new float2(cfg.Weapon.ProjectileSpeed, 0f), BodyBand(cfg), 0f,
                    cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                    ownerIndex: 0);
                world.ClearEvents();
                TickIdle(world);
                return world;
            }

            Assert.Less(playerX - muzzleX, c.Hero.Radius + c.Weapon.ProjectileRadius,
                "fixture premise: the muzzle sits inside player 1's padded circle (t = 0)");
            Assert.Less(muzzleX - obstacleX, obstacleRadius + c.Weapon.ProjectileRadius,
                "fixture premise: the muzzle sits inside the obstacle's padded circle (t = 0)");
            Assert.Greater(playerX - obstacleX, obstacleRadius + c.Hero.Radius,
                "fixture premise: player 1 does not overlap the obstacle and stays where placed");

            SimulationWorld tie = Fire(withObstacle: true);
            Assert.AreEqual(1, TestEvents.CountOf(tie, SimEventKind.ProjectileBlocked),
                "the barrier is packed first, so it takes an exact tie");
            Assert.AreEqual(c.Hero.MaxHp, tie.PlayerAt(1).Hp, 1e-4f);
            Assert.AreEqual(0, TestEvents.CountOf(tie, SimEventKind.PlayerDamaged));

            // Control: without the obstacle the very same round does reach
            // player 1 — so the tie above was a real contest between two
            // gathered candidates, not a player the gather had simply missed.
            SimulationWorld noTie = Fire(withObstacle: false);
            Assert.Less(noTie.PlayerAt(1).Hp, c.Hero.MaxHp,
                "with nothing to outrank it, the player candidate resolves");
        }

        [Test]
        public void MobOutranksPlayerOnAnExactTie()
        {
            // Second of the three packing-order pairs (see
            // BarrierOutranksPlayerOnAnExactTie for the first): mobs are packed
            // BEFORE players, so a mob wins an exact tie against one. Mirror of
            // ProjectileHeightTests.EqualT_TieBreaksLowerIndex, with its second
            // chaser replaced by a player — that fixture pins mob-vs-mob and is
            // blind to this pair.
            //
            // The tie is EXACT by construction, not by luck: the two bodies are
            // mirrored across the firing line and given the same radius, so
            // Geometry.SegmentCircle solves a quadratic whose every coefficient
            // is bit-identical for both (f.x and r are shared, and f.y only
            // enters squared, which erases the sign exactly). The loop below
            // re-derives both roots off the very segment the tick is about to
            // sweep and asserts equality with a ZERO delta.
            var c = Range();
            c.Chaser.Radius = c.Hero.Radius; // the shared radius that makes the tie exact
            float halfGap = c.Chaser.AttackRange;
            const float sweepRadius = 1f;
            Assert.Less(halfGap, sweepRadius + c.Hero.Radius,
                "fixture premise: the round is fat enough to reach both bodies at once");
            Assert.Greater(2f * halfGap, c.Chaser.AttackRange,
                "fixture premise: the two bodies are far enough apart that the chaser never "
                + "telegraphs a strike of its own into this measurement");

            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);                   // shooter, at the muzzle
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(5f, -halfGap));      // player candidate
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(5f, halfGap)));
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(c.Weapon.ProjectileSpeed, 0f), BodyBand(c), 0f,
                c.Chaser.MaxHp * 10f, sweepRadius, c.Weapon.ProjectileLifetime, ownerIndex: 0);

            var inputs = new SimInput[2];
            bool tied = false;
            for (int i = 0; i < 30 && w.ProjectileCount > 0; i++)
            {
                // The same segment ProjectileSystem is about to build for this
                // tick (startPos + Vel * TickDt), so these are ITS roots, not an
                // approximation of them. Neither body moves — Range() zeroes the
                // chaser's speed and the players get idle input — so reading the
                // positions before the tick is exact too.
                ProjectileState round = w.GetProjectileForTest(0);
                float2 start = round.Pos;
                float2 end = start + round.Vel * SimulationWorld.TickDt;
                bool onMob = Geometry.SegmentCircle(start, end, round.Radius,
                    w.Mobs[0].Pos, c.Chaser.Radius, out float tMob);
                bool onPlayer = Geometry.SegmentCircle(start, end, round.Radius,
                    w.PlayerAt(1).Pos, c.Hero.Radius, out float tPlayer);
                if (onMob || onPlayer)
                {
                    Assert.IsTrue(onMob && onPlayer,
                        "fixture premise: both bodies enter the sweep on the SAME tick");
                    Assert.AreEqual(tMob, tPlayer, 0f,
                        "fixture premise: the tie must be EXACT — a merely approximate one "
                        + "measures the quadratic, not the tie-break");
                    tied = true;
                }
                w.TickAll(inputs);
            }

            Assert.IsTrue(tied, "the round never reached the two bodies");
            Assert.AreEqual(0, w.MobCount, "the mob is packed first, so it takes the tie");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out _));
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(1).Hp, 1e-4f,
                "the player lost the tie and must be untouched");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.PlayerDamaged));
        }

        [Test]
        public void PlayerOutranksFloorOnAnExactTie()
        {
            // Third packing-order pair: players are packed BEFORE the floor, so a
            // player wins an exact tie against it.
            //
            // Both candidates are pinned to exactly zero, which is the only tie
            // between these two that is exact rather than lucky — they come from
            // different formulas, so no shared quadratic makes their roots agree
            // bit for bit. The round spawns INSIDE the victim's padded circle, so
            // Geometry.SegmentCircle takes its start-inside branch and reports a
            // literal 0; and its launch height equals its own radius, so
            // ProjectileSystem's tFloor = (Radius - Height) / (VelZ * TickDt) has
            // an exactly-zero numerator. (IEEE signed zeroes compare equal, and
            // the min-scan's strict `<` therefore keeps whichever was packed
            // first — which is the whole point of this test.)
            var c = Range();
            var w = new SimulationWorld(1, c, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 20f)); // shooter, well off the muzzle
            var victimPos = new float2(5f, 0f);
            TestWorlds.RelocatePlayerForTest(w, 1, victimPos);

            float sweepRadius = c.Weapon.ProjectileRadius;
            float launchHeight = sweepRadius; // exactly-zero tFloor numerator
            const float plungeVelZ = -60f;    // descending, so the floor candidate is gathered
            w.SpawnProjectileForTest(ProjectileOwner.Player, victimPos,
                new float2(c.Weapon.ProjectileSpeed, 0f), launchHeight, plungeVelZ,
                c.Weapon.Damage, sweepRadius, c.Weapon.ProjectileLifetime, ownerIndex: 0);

            ProjectileState round = w.GetProjectileForTest(0);
            float2 end = round.Pos + round.Vel * SimulationWorld.TickDt;
            Assert.IsTrue(Geometry.SegmentCircle(round.Pos, end, round.Radius, victimPos,
                c.Hero.Radius, out float tPlayer));
            float tFloor = (round.Radius - round.Height) / (round.VelZ * SimulationWorld.TickDt);
            Assert.AreEqual(tPlayer, tFloor, 0f,
                "fixture premise: the two candidates tie EXACTLY");
            Assert.GreaterOrEqual(tFloor, 0f, "fixture premise: the floor candidate is gathered");
            Assert.LessOrEqual(tFloor, 1f, "fixture premise: the floor crossing falls in this step");

            w.ClearEvents();
            TickIdle(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDamaged),
                "the player is packed before the floor, so the player takes the tie");
            Assert.Less(w.PlayerAt(1).Hp, c.Hero.MaxHp);
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "the floor lost the tie — no ProjectileBlocked may fire");
        }

        [Test]
        public void LowerPlayerIndexWinsAnExactTie()
        {
            // Ф4 fix-wave. The three tests above pin players against the OTHER
            // candidate kinds (barrier, mob, floor); this pins the order WITHIN
            // the player loop, which nothing else did. ProjectileSystem gathers
            // players by ascending index and the min-scan breaks ties with a
            // strict `<`, so an exact tie goes to the LOWEST player index — the
            // same rule ProjectileHeightTests.EqualT_TieBreaksLowerIndex pins
            // for mobs, and the only one of the four still unwitnessed.
            // Its consequences are macroscopic rather than cosmetic: reverse
            // the loop and a different player takes the blow, which moves Hp,
            // DamageTaken, credit, possibly a death — and with them the state
            // hash the two goldens pin.
            //
            // The tie is EXACT by construction, same argument as
            // MobOutranksPlayerOnAnExactTie: the two victims are mirrored
            // across the firing line and share Hero.Radius by DEFINITION (one
            // config field, both bodies — no fixture assignment needed as
            // there), so Geometry.SegmentCircle solves a quadratic whose every
            // coefficient is bit-identical for both (f.x and r are shared, f.y
            // only enters squared, which erases the sign exactly). The loop
            // below re-derives both roots off the very segment the tick is
            // about to sweep and asserts equality with a ZERO delta.
            var c = Range();
            const float sweepRadius = 1f;
            float halfGap = c.Hero.Radius + 0.2f;
            Assert.Less(halfGap, sweepRadius + c.Hero.Radius,
                "fixture premise: the round is fat enough to reach both victims at once");
            Assert.Greater(2f * halfGap, 2f * c.Hero.Radius,
                "fixture premise: the two victims do not overlap, so neither is nudged off "
                + "its mirrored mark");

            const float victimX = 5f;
            var w = new SimulationWorld(1, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);                      // shooter, at the muzzle
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(victimX, halfGap));     // packed first
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(victimX, -halfGap));    // packed second
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(c.Weapon.ProjectileSpeed, 0f), BodyBand(c), 0f,
                c.Weapon.Damage, sweepRadius, c.Weapon.ProjectileLifetime, ownerIndex: 0);

            var inputs = new SimInput[3];
            bool tied = false;
            for (int i = 0; i < 30 && w.ProjectileCount > 0; i++)
            {
                // The same segment ProjectileSystem is about to build for this
                // tick (startPos + Vel * TickDt), so these are ITS roots. No
                // body moves — the players get idle input and nothing pushes
                // them — so reading the positions before the tick is exact too.
                ProjectileState round = w.GetProjectileForTest(0);
                float2 start = round.Pos;
                float2 end = start + round.Vel * SimulationWorld.TickDt;
                bool onLow = Geometry.SegmentCircle(start, end, round.Radius,
                    w.PlayerAt(1).Pos, c.Hero.Radius, out float tLow);
                bool onHigh = Geometry.SegmentCircle(start, end, round.Radius,
                    w.PlayerAt(2).Pos, c.Hero.Radius, out float tHigh);
                if (onLow || onHigh)
                {
                    Assert.IsTrue(onLow && onHigh,
                        "fixture premise: both victims enter the sweep on the SAME tick");
                    Assert.AreEqual(tLow, tHigh, 0f,
                        "fixture premise: the tie must be EXACT — a merely approximate one "
                        + "measures the quadratic, not the tie-break");
                    tied = true;
                }
                w.TickAll(inputs);
            }

            Assert.IsTrue(tied, "the round never reached the two victims");
            Assert.Less(w.PlayerAt(1).Hp, c.Hero.MaxHp,
                "player 1 is gathered before player 2, so it takes the tie");
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(2).Hp, 1e-4f,
                "player 2 lost the tie and must be untouched");
            Assert.AreEqual(c.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-4f,
                "the shooter is never gathered by its own round");
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDamaged),
                "single-target: exactly one victim, no piercing");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(1, damaged.PlayerIndex, "PlayerDamaged carries the winning victim's index");
        }

        [Test]
        public void SaturatedWorld_CandidateScratchDoesNotOverflow()
        {
            // Upper bound on ONE round's candidate gather: interior barrier +
            // ring boundary + every live mob + every live player but the owner +
            // floor, which is what sizes the scratch to MaxMobs + MaxPlayers + 3.
            // This fixture packs all five groups on a single tick — a full mob
            // roster, three players, a sweep radius wide enough to overlap every
            // body at t = 0 (Geometry.SegmentCircle's start-inside branch), an
            // obstacle within that radius for the interior barrier slot, a
            // launch point close enough to the padded rim that the boundary
            // crossing falls inside this very step, and a descending flight
            // whose ground crossing does too.
            //
            // Stage 2 Task 46 widened both the bound and this fixture: the
            // barrier used to be ONE slot standing for obstacles, walls and the
            // rim together, and the pre-46 version of this test launched from
            // the origin, where the rim is far out of reach for a single step.
            var c = TestConfigs.Quiet(); // Default arena (obstacles + walls), waves out of reach
            var w = new SimulationWorld(1, c, playerCount: 3);
            TestWorlds.SpawnMobsToCap(w);
            Assert.AreEqual(c.Arena.MaxMobs, w.MobCount, "fixture premise: every mob slot is filled");
            Assert.AreEqual(3, w.PlayerCount);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);             // shooter
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(TargetX, 0f)); // victim of the aimed round below
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, TargetX)); // bystander, still inside the wide sweep

            // Wide enough to swallow the whole crowd from the launch point
            // below, which sits ~8 m out rather than at the origin.
            const float sweepRadius = 57f;
            const float plungeVelZ = -60f;  // tFloor = (Radius - Height) / (VelZ * TickDt) = 0.5
            const float launchHeight = sweepRadius + 0.5f;
            // Just inside the boundary's own padded rim (Arena.Radius -
            // sweepRadius), moving outward, so SegmentRingWall's crossing lands
            // inside this step instead of dozens of steps away.
            const float launchBack = 0.3f;
            float ringLimit = c.Arena.Radius - sweepRadius;
            var launchPos = new float2(ringLimit - launchBack, 0f);
            Assert.Less(launchBack, c.Weapon.ProjectileSpeed * SimulationWorld.TickDt,
                "fixture premise: the boundary crossing falls inside this one step — otherwise the "
                + "ring candidate is never gathered and the worst case is one slot short");

            // Ф4 fix-wave, rescaled by Stage 2 Task 46: the worst case this test
            // exists to size the scratch against is 2 barrier slots + MaxMobs +
            // 2 other players + 1 floor = 101 candidates. What keeps every mob
            // in the sweep is the round starting inside each of their padded
            // circles (Geometry.SegmentCircle's start-inside branch, t = 0)
            // together with SpawnMobsToCap's own radius spread — and that
            // spread lives in another file and can move without this one
            // noticing. So it is asserted here, not merely narrated above.
            for (int m = 0; m < w.MobCount; m++)
                Assert.Less(math.distance(w.Mobs[m].Pos, launchPos), sweepRadius,
                    "fixture premise: every mob must fall inside the sweep — otherwise the worst-case "
                    + "candidate count is never actually reached and a too-small scratch passes for free");
            for (int p = 1; p < w.PlayerCount; p++)
                Assert.Less(math.distance(w.PlayerAt(p).Pos, launchPos), sweepRadius,
                    "fixture premise: every non-owner player is inside the sweep too");

            // The bound itself, not just its symptom. An overflow throws only
            // once the count passes the LENGTH, so a scratch sized exactly to
            // the worst case would survive the tick below in silence — this is
            // what pins the one slot of slack the field's own doc promises,
            // and it is stated as the arithmetic rather than as a literal.
            int worstCase = c.Arena.MaxMobs + (c.Arena.MaxPlayers - 1) + 3;
            Assert.Greater(w.ProjCandidates.Length, worstCase,
                "the candidate scratch must hold the worst-case gather with room to spare — "
                + "2 barrier slots + every mob + every non-owner player + floor");

            w.SpawnProjectileForTest(ProjectileOwner.Player, launchPos,
                new float2(c.Weapon.ProjectileSpeed, 0f), launchHeight, plungeVelZ,
                c.Weapon.Damage, sweepRadius, c.Weapon.ProjectileLifetime, ownerIndex: 0);
            // A second, ordinary round on the same tick: the players really are
            // in the candidate set — which is what makes the wider scratch
            // necessary — rather than merely being tolerated by it.
            TestWorlds.FireAimed3D(w, float2.zero, BodyBand(c), new float2(TargetX, 0f), BodyBand(c),
                ownerIndex: 0);

            var inputs = new SimInput[3];
            Assert.DoesNotThrow(() => w.TickAll(inputs),
                "the candidate scratch must hold barrier + every mob + every other player + floor");
            Assert.Less(w.PlayerAt(1).Hp, c.Hero.MaxHp,
                "the aimed round must still reach its victim through the crowd");
        }
    }
}
