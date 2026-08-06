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
        public void SaturatedWorld_CandidateScratchDoesNotOverflow()
        {
            // Upper bound on ONE round's candidate gather: barrier + every live
            // mob + every live player but the owner + floor, which is what sizes
            // the scratch to MaxMobs + MaxPlayers + 2. This fixture packs all
            // four groups on a single tick — a full mob roster, three players, a
            // sweep radius wide enough to overlap every body at t = 0
            // (Geometry.SegmentCircle's start-inside branch), an obstacle within
            // that radius for the barrier slot, and a descending flight whose
            // ground crossing falls inside this very step for the floor slot.
            var c = TestConfigs.Quiet(); // Default arena (obstacles + walls), waves out of reach
            var w = new SimulationWorld(1, c, playerCount: 3);
            TestWorlds.SpawnMobsToCap(w);
            Assert.AreEqual(c.Arena.MaxMobs, w.MobCount, "fixture premise: every mob slot is filled");
            Assert.AreEqual(3, w.PlayerCount);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);             // shooter
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(TargetX, 0f)); // victim of the aimed round below
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, TargetX)); // bystander, still inside the wide sweep

            const float sweepRadius = 40f;  // SpawnMobsToCap tops out near 32 m from the origin
            const float plungeVelZ = -60f;  // tFloor = (Radius - Height) / (VelZ * TickDt) = 0.25
            const float launchHeight = sweepRadius + 0.5f;
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
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
