using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// bd `app-s0u` — the client-side tracer rebuild, measured against the
    /// authority rather than argued from algebra.
    ///
    /// THE SHAPE IS `TrajectoryTests.MatchesTheSimulationsOwnFloorCut`'s, and
    /// that is deliberate (the audit named it as the pattern): a REAL round is
    /// fired through `SimulationWorld`/`ProjectileSystem`, the reconstruction is
    /// fed exactly what the wire carries about that round, and both are stepped
    /// side by side. A test that restated `Pos += Vel * dt` would agree with a
    /// wrong implementation as happily as with a right one.
    public class TracerProjectilesTests
    {
        const int SpawnTick = 100;

        static SimConfig Range()
        {
            var c = TestConfigs.Open();
            c.Weapon.SpreadRad = 0f;
            c.Weapon.RecoilPerShotRad = 0f;
            return c;
        }

        static bool TryFindInWorld(SimulationWorld w, int id, out ProjectileState found)
        {
            for (int i = 0; i < w.ProjectileCount; i++)
            {
                if (w.Projectiles[i].Id != id) continue;
                found = w.Projectiles[i];
                return true;
            }
            found = default;
            return false;
        }

        /// Fires one round at `targetH` and hands back the world plus the
        /// round's state as the WIRE describes it.
        static SimulationWorld Fire(in SimConfig c, float targetH, out ProjectileState spawned)
        {
            var w = new SimulationWorld(1, c);
            float muzzle = c.Hero.MuzzleHeight;
            TestWorlds.FireAimed3D(w, float2.zero, muzzle, new float2(30f, 0f), targetH);
            Assert.AreEqual(1, w.ProjectileCount, "fixture premise: exactly one round in flight");
            spawned = w.Projectiles[0];
            return w;
        }

        /// app-88jb Т32: the tracer's constructor grew the world's own numbers
        /// (it cranks `ProjectileFlight` now) and this client's catch-up budget
        /// (`NetConfig.TracerCatchUpBudget`). NEITHER MATTERS TO ANY FIXTURE IN
        /// THIS FILE, and that is the point rather than a convenience: not one
        /// of the fourteen calls `StepTo`, so not one of them moves the cache,
        /// so every answer below is the closed form measured from a cache still
        /// standing on the spawn tick — bit for bit what this class answered
        /// before the task (coordinator Ruling 287). The fixtures that ARE
        /// about the stepped flight live in `TracerFlightTests`, and the budget
        /// they state is their own. One shared config and the shipped budget,
        /// through one helper, so a future constructor change lands in one line
        /// here instead of fourteen.
        static readonly SimConfig TableCfg = TestConfigs.Open();
        const int ShippedCatchUpBudget = 8;   // NetConfig.TracerCatchUpBudget's C# default

        static TracerProjectiles NewTable(int capacity)
            => new TracerProjectiles(capacity, in TableCfg, ShippedCatchUpBudget);

        static TracerProjectiles TrackerFor(in ProjectileState s, int capacity = 8)
        {
            var tracers = NewTable(capacity);
            Assert.IsTrue(tracers.TrySpawn(s.Id, SpawnTick, s.Pos, s.Height,
                    math.normalizesafe(s.Vel), math.length(s.Vel), s.VelZ, s.Radius, s.Ttl),
                "a round the client can see must be accepted");
            return tracers;
        }

        /// The flat case: no vertical component at all, so this one pins the
        /// horizontal half and nothing else.
        [Test]
        public void ReproducesTheSimulationsOwnFlight_TickForTick()
        {
            var c = Range();
            var w = Fire(in c, c.Hero.MuzzleHeight, out ProjectileState s);
            var tracers = TrackerFor(in s);
            var scratch = new ProjectileState[8];

            for (int age = 1; age <= 8; age++)
            {
                w.Tick(default);

                Assert.IsTrue(TryFindInWorld(w, s.Id, out ProjectileState authoritative),
                    $"fixture premise: the round is still in flight at tick {age}");
                Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick + age),
                    $"the tracer must still be drawn at tick {age}");

                Assert.AreEqual(authoritative.Pos.x, scratch[0].Pos.x, 1e-3f,
                    $"x must match the authority at tick {age}");
                Assert.AreEqual(authoritative.Pos.y, scratch[0].Pos.y, 1e-3f,
                    $"y must match the authority at tick {age}");
                Assert.AreEqual(authoritative.PrevPos.x, scratch[0].PrevPos.x, 1e-3f,
                    $"PrevPos is what the renderer interpolates from at tick {age}");
            }
        }

        /// The flat test above cannot see the vertical half at all — a round
        /// fired level has `VelZ == 0`, so dropping the height step entirely
        /// would still pass it. This one climbs.
        [Test]
        public void ReproducesTheClimbOfAnAimedRound_TickForTick()
        {
            var c = Range();
            var w = Fire(in c, c.Hero.MuzzleHeight + 6f, out ProjectileState s);
            Assert.Greater(s.VelZ, 0.5f, "fixture premise: this round genuinely climbs");

            var tracers = TrackerFor(in s);
            var scratch = new ProjectileState[8];
            float startHeight = s.Height;

            for (int age = 1; age <= 8; age++)
            {
                w.Tick(default);

                Assert.IsTrue(TryFindInWorld(w, s.Id, out ProjectileState authoritative),
                    $"fixture premise: still in flight at tick {age}");
                Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick + age));

                Assert.AreEqual(authoritative.Height, scratch[0].Height, 1e-3f,
                    $"height must match the authority at tick {age}");
                Assert.AreEqual(authoritative.PrevHeight, scratch[0].PrevHeight, 1e-3f,
                    $"PrevHeight must match the authority at tick {age}");
            }

            Assert.Greater(scratch[0].Height, startHeight + 0.5f,
                "and the round really did rise over the run — otherwise the two agreeing "
                + "means only that both stood still");
        }

        /// The property the closed form exists for: the answer at a tick does
        /// not depend on having been asked about the ticks before it.
        [Test]
        public void ASkippedFrameCostsTheTracerNothing()
        {
            var c = Range();
            Fire(in c, c.Hero.MuzzleHeight + 4f, out ProjectileState s);

            var stepped = TrackerFor(in s);
            var jumped = TrackerFor(in s);
            var a = new ProjectileState[4];
            var b = new ProjectileState[4];

            for (int age = 1; age <= 6; age++) stepped.WriteInto(a, SpawnTick + age);
            Assert.AreEqual(1, jumped.WriteInto(b, SpawnTick + 6), "asked only once, at the end");

            Assert.AreEqual(a[0].Pos.x, b[0].Pos.x, 1e-6f,
                "a client that dropped five frames must draw the round exactly where a client "
                + "that drew all of them draws it");
            Assert.AreEqual(a[0].Height, b[0].Height, 1e-6f, "and at the same height");
            Assert.AreEqual(a[0].PrevPos.x, b[0].PrevPos.x, 1e-6f,
                "including the previous pair, which is a function of the clock too");
        }

        [Test]
        public void ARoundIsNotDrawnBeforeTheTickItWasFiredOn()
        {
            var c = Range();
            Fire(in c, c.Hero.MuzzleHeight, out ProjectileState s);
            var tracers = TrackerFor(in s);
            var scratch = new ProjectileState[4];

            Assert.AreEqual(0, tracers.WriteInto(scratch, SpawnTick - 1),
                "the render clock has not reached the shot yet — the muzzle flash has not "
                + "played either, and a bullet ahead of its own flash is the artifact this "
                + "clock discipline exists to prevent");
            Assert.AreEqual(1, tracers.Count, "but the round is tracked, waiting for its tick");
            Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick),
                "and on its own tick it appears, standing at the muzzle");
            Assert.AreEqual(s.Pos.x, scratch[0].Pos.x, 1e-6f);
            Assert.AreEqual(scratch[0].Pos.x, scratch[0].PrevPos.x, 1e-6f,
                "with nothing behind it to interpolate from");
        }

        [Test]
        public void CarriesTheRadiusTheRendererDrawsWith()
        {
            var c = Range();
            Fire(in c, c.Hero.MuzzleHeight, out ProjectileState s);
            var tracers = TrackerFor(in s);

            var scratch = new ProjectileState[4];
            Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick));
            Assert.AreEqual(s.Radius, scratch[0].Radius, 1e-6f,
                "ViewRegistry.SyncProjectiles sizes the sphere off Radius, and the wire does "
                + "not carry it — it comes from the config by owner");
            Assert.AreEqual(s.Id, scratch[0].Id, "the id is what ProjectileEnded retires by");
        }

        [Test]
        public void ARetiredRoundFliesUntilTheClockReachesItsEnd()
        {
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            Assert.IsTrue(tracers.TrySpawn(11, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f));
            Assert.IsTrue(tracers.Retire(11, SpawnTick + 5), "the server ended it five ticks later");

            var scratch = new ProjectileState[4];
            Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick + 4),
                "before that tick the round is still in the air — the impact has not been "
                + "shown yet either");
            Assert.AreEqual(0, tracers.WriteInto(scratch, SpawnTick + 5),
                "and on the ending tick it is gone, together with the impact");
            Assert.AreEqual(1, tracers.Count,
                "writing never mutates — see Prune's own doc for why that matters to the pair");

            tracers.Prune(SpawnTick + 5);
            Assert.AreEqual(0, tracers.Count, "the table drops it rather than keeping a corpse");
        }

        [Test]
        public void RetireNamesOneRound_AndAnUnknownIdIsARefusal()
        {
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            tracers.TrySpawn(11, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f);
            tracers.TrySpawn(22, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f);

            Assert.IsFalse(tracers.Retire(33, SpawnTick), "a round this client never saw fired");
            Assert.IsTrue(tracers.Retire(11, SpawnTick + 1));

            var scratch = new ProjectileState[4];
            Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick + 1));
            Assert.AreEqual(22, scratch[0].Id, "the survivor is the one that was not named");
        }

        [Test]
        public void ARoundNeverEndsItselfWhileTheServerHasNotSaidSo()
        {
            var tracers = NewTable(4);
            // A Ttl far shorter than the run below: the round would have expired
            // several times over in the simulation, and here it must not — every
            // ending is the server's to declare (CR 3).
            Assert.IsTrue(tracers.TrySpawn(7, SpawnTick, float2.zero, 1f, new float2(1f, 0f),
                10f, 0f, 0.1f, ttl: 0.05f));

            var scratch = new ProjectileState[4];
            Assert.AreEqual(1, tracers.WriteInto(scratch, SpawnTick + 30),
                "the client owns no outcome — only ProjectileEnded retires a tracer");
            Assert.Less(scratch[0].Ttl, 0f, "even with its own lifetime long spent");
        }

        [Test]
        public void ResetDropsEverything_SoAMatchRestartStartsEmpty()
        {
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            tracers.TrySpawn(1, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f);
            tracers.TrySpawn(2, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f);

            tracers.Reset();

            Assert.AreEqual(0, tracers.Count, "rounds of the match that ended must not outlive it");
            var scratch = new ProjectileState[4];
            Assert.AreEqual(0, tracers.WriteInto(scratch, SpawnTick + 1));
            Assert.IsTrue(tracers.TrySpawn(1, 0, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f),
                "and an id from the previous match is free to be minted again");
        }

        [Test]
        public void ADuplicateIdIsRefused_NotTrackedTwice()
        {
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            Assert.IsTrue(tracers.TrySpawn(5, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f));
            Assert.IsFalse(tracers.TrySpawn(5, SpawnTick + 2, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f),
                "one live id is one round");
            Assert.AreEqual(1, tracers.Count);
        }

        [Test]
        public void AFullTableRefusesRatherThanOverwriting()
        {
            var tracers = NewTable(2);
            var dir = new float2(1f, 0f);
            Assert.IsTrue(tracers.TrySpawn(1, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f));
            Assert.IsTrue(tracers.TrySpawn(2, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f));

            Assert.IsFalse(tracers.TrySpawn(3, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f),
                "a refusal is a value, never an exception — this runs inside a broadcast "
                + "handler (Р82/195)");
            Assert.AreEqual(2, tracers.Count, "and the rounds already tracked are untouched");
        }

        // ---- Т31: the one field the wire drops that this side can answer for ----

        /// app-88jb Т31 (findings A-Т31-6, coordinator Rulings 247/256). THE
        /// ROUND'S OWNER IS NOT ON THE WIRE FOR AN ENDING, and the client needs
        /// it: the moment a hit applies is `projectileMass * speed3D / mass`,
        /// and both of the first two terms fork on WHO fired — a collector's
        /// weapon against a Gunner archetype's. Decoded as a mob's round, a
        /// player's own shot rebuilds a blow several times weaker than the one
        /// the server resolved. The answer this side still has is here, in the
        /// table the spawn record already filled.
        ///
        /// BOTH RAILS AND A MISS, in one fixture: an owner byte that always
        /// answered "the collector in seat 2" would satisfy the first half
        /// alone, and one that always answered NoOwner would satisfy the
        /// second alone. The stranger's half is the one that says a refusal is
        /// a VALUE — this is asked from inside the snapshot receive path.
        [Test]
        public void TryGetOwner_AnswersTheOwnerOfALiveRound_AndNothingForAStranger()
        {
            const byte shooterSlot = 2;      // not 0, which every unfilled byte reads as
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            Assert.IsTrue(tracers.TrySpawn(11, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f,
                    ProjectileOwner.Player, shooterSlot),
                "fixture premise: the collector's round is tracked");
            Assert.IsTrue(tracers.TrySpawn(22, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f,
                    ProjectileOwner.Mob, ProjectileIds.NoOwner),
                "fixture premise: the gunner's round is tracked too");

            Assert.IsTrue(tracers.TryGetOwner(11, out ProjectileOwner owner, out byte ownerIndex),
                "владелец живого раунда не отвечает — восстанавливать стрелка неоткуда");
            Assert.AreEqual(ProjectileOwner.Player, owner,
                "a round the collector fired must come back as the collector's");
            Assert.AreEqual(shooterSlot, ownerIndex,
                "and it must name the SEAT the spawn record carried, not seat 0");

            Assert.IsTrue(tracers.TryGetOwner(22, out ProjectileOwner mobOwner, out byte mobIndex),
                "the gunner's round must be answerable too");
            Assert.AreEqual(ProjectileOwner.Mob, mobOwner,
                "a mob's round must not come back on the collector's rail — the mass and the speed "
                + "cap it selects differ by a factor the blow is plainly built out of");
            Assert.AreEqual(ProjectileIds.NoOwner, mobIndex,
                "and its seat must stay the no-owner sentinel rather than becoming a real one");

            Assert.IsFalse(tracers.TryGetOwner(33, out _, out byte noneIndex),
                "a round this client never saw fired is a REFUSAL, not an exception — this is asked "
                + "inside the snapshot receive path (Р82/195)");
            // The seat, not the owner enum: `ProjectileOwner`'s zero is
            // `Player`, a REAL rail rather than an absence, so it can carry no
            // claim about a miss. `NoOwner` is a genuine sentinel and is the
            // half worth pinning — a miss leaves it behind exactly as
            // RestoreMobType leaves a zero type.
            Assert.AreEqual(ProjectileIds.NoOwner, noneIndex,
                "and a miss must leave the sentinel behind");
        }

        /// WHEN the answer stops being available, asserted rather than assumed
        /// (Ruling 247: the lookup happens BEFORE `Retire` in the decode loop,
        /// so the ordering has to be a fact about the table and not a hope
        /// about the caller). Measured off `Prune`'s own body: an ended round
        /// leaves the table on the first prune whose tick has REACHED its
        /// `EndTick`, and not before — the same `renderTick >= EndTick` test
        /// `WriteInto` uses to stop drawing it.
        [Test]
        public void TryGetOwner_StillAnswersAfterRetire_UntilPruned()
        {
            const byte shooterSlot = 2;
            var tracers = NewTable(4);
            Assert.IsTrue(tracers.TrySpawn(11, SpawnTick, float2.zero, 1f, new float2(1f, 0f),
                    10f, 0f, 0.1f, 2f, ProjectileOwner.Player, shooterSlot),
                "fixture premise: the round is tracked");

            Assert.IsTrue(tracers.TryGetOwner(11, out _, out byte before),
                "владелец живого раунда не отвечает");
            Assert.AreEqual(shooterSlot, before, "witness: the seat is the one the spawn carried");

            Assert.IsTrue(tracers.Retire(11, SpawnTick + 5), "the server ended it five ticks later");
            Assert.IsTrue(tracers.TryGetOwner(11, out _, out byte afterRetire),
                "an ended round must still name its owner: the decode loop asks this BEFORE it "
                + "routes the ending to Retire, and a record that vanished on the ending would "
                + "leave every hit rebuilt on the wrong shooter");
            Assert.AreEqual(shooterSlot, afterRetire, "and it is still the same seat");

            tracers.Prune(SpawnTick + 4);
            Assert.IsTrue(tracers.TryGetOwner(11, out _, out _),
                "a prune BEFORE the ending tick drops nothing — the round is still in the air");

            tracers.Prune(SpawnTick + 5);
            Assert.IsFalse(tracers.TryGetOwner(11, out _, out byte afterPrune),
                "and once the clock reaches the ending the record is gone: the table drops it "
                + "rather than keeping a corpse, and this accessor must not resurrect one");
            Assert.AreEqual(ProjectileIds.NoOwner, afterPrune,
                "leaving the sentinel behind, as every other miss does");
        }

        [Test]
        public void WriteIntoNeverOverrunsTheDestination()
        {
            var tracers = NewTable(4);
            var dir = new float2(1f, 0f);
            for (int i = 1; i <= 4; i++)
                tracers.TrySpawn(i, SpawnTick, float2.zero, 1f, dir, 10f, 0f, 0.1f, 2f);

            var small = new ProjectileState[2];
            Assert.AreEqual(2, tracers.WriteInto(small, SpawnTick),
                "a destination smaller than the table is filled to its own length, not past it");
        }
    }
}
