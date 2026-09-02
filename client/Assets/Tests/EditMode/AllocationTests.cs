using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// Task 42b split `EventRelevance.ShouldDeliver`'s single `observerIndex`
    /// into `identityIndex`/`viewpointIndex`. The three call sites below pass
    /// the SAME value for both — this file measures allocations, not
    /// spectating behavior, so the extra argument pins nothing new.
    public class AllocationTests
    {
        [Test]
        public void Tick_DoesNotAllocateGC()
        {
            var w = TestWorlds.Saturated(out SimConfig config);
            // Sanity-check the fixture itself before measuring: every mob slot
            // must actually be filled AT THE MOMENT OF MEASUREMENT — live
            // population, not cumulative spawn events. Stage 3 Task 5 fix-round
            // 1 (spec Р252, coordinator R-22): the OLD proxy asserted
            // `TestEvents.CountOf(w, SimEventKind.MobSpawned) ==
            // config.Arena.MaxMobs`, reading "one MobSpawned per mob, nothing
            // clears the buffer across the 100-tick warm-up" as "population is
            // saturated" — friendly fire broke that reading (a Gunner's own
            // round can now connect with a neighboring mob in Saturated's
            // packed golden-angle crowd instead of sailing through untouched,
            // and each wave refill of a dead slot is its own EXTRA
            // MobSpawned), so the cumulative count legitimately exceeded the
            // cap while nobody had checked whether the LIVE population still
            // did. Fix-round 1 replaced the proxy with a direct `w.MobCount`
            // read — CORRECT IN FORM, still WRONG IN VALUE that round
            // (`92`, not `96`): the wave's own refill had not caught the
            // friendly-fire losses within the 100-tick warm-up window, so the
            // direct read just told the truth the proxy had been hiding — the
            // fixture itself was no longer saturated on return, which is a
            // real defect in `TestWorlds.Saturated`, not a wrong assertion.
            // Fix-round 2 closes THAT gap inside `Saturated` itself (a second
            // `SpawnMobsToCap` call right after the warm-up — see its own
            // doc), so this read is now checking a promise the builder
            // actually keeps, not working around one it doesn't.
            Assert.AreEqual(config.Arena.MaxMobs, w.MobCount);
            // F-4 fix-round (ledger T29): the fixture's whole point is a world
            // under sustained fire — its 100-tick hold-fire warm-up must have
            // actually produced live projectiles for the allocation measurement
            // below to be exercising the projectile hot path at all, not just an
            // idle mob crowd.
            Assert.Greater(w.ProjectileCount, 0);

            var input = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f) };
            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++) w.Tick(input);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void SaturatedTrio_TicksWithoutAllocations()
        {
            // Stage 2 Task 18 (Phase Ф4 hardening test): Tick_DoesNotAllocateGC
            // above never runs Tick(in SimInput)'s multiplayer sibling at all.
            // Task 17 widened the candidate scratch to MaxMobs + MaxPlayers + 2
            // and added a per-live-player gather loop inside ProjectileSystem
            // that only a playerCount > 1 world ever enters, and TickAll itself
            // steps a per-player input array Tick(in SimInput) never touches.
            // This is the first allocation measurement to tick that world at all.
            //
            // measuredTicks is threaded into TrioSaturated (fix-round 1, I-1) so
            // its own Hp budget is derived from the SAME loop length used below,
            // not a constant living in a different file that could silently
            // drift out of sync with it.
            //
            // Т4 (app-ggvz, spec §3.3) EXTENDED THIS TEST rather than adding a
            // second one: the per-ring cadence put two new things on the hot
            // path, and both of them run inside the measured window below.
            // (1) A FULL SCAN OF THE MOB ARRAY every tick, tallying the living
            //     by MobState.SpawnZone — this fixture is saturated to
            //     Arena.MaxMobs, so that scan is as long here as it will ever
            //     be in a real raid.
            // (2) A `System.Span<int> alive = stackalloc int[Zones.Count]` per
            //     tick — the stack, never the heap. The precedent it follows is
            //     the one SplitByZones used to hold before Т4 deleted it, and
            //     the reason it is stated out loud is that an earlier task
            //     already lost a buffer to the heap on this very path (the
            //     reweighting variant of Р253's core-budget move, caught by
            //     THIS test).
            // The fixture is ZONELESS (TrioSaturated's own doc), so the run
            // also covers the frozen-ring branch — Middle and Core are reset
            // every tick — alongside Outer's live one.
            const int measuredTicks = 1000;
            var w = TestWorlds.TrioSaturated(out SimConfig config, measuredTicks);

            // Same fixture-sanity discipline as Tick_DoesNotAllocateGC above:
            // prove the world is actually loaded before measuring it, not an
            // empty stage that would pass this test for free.
            Assert.AreEqual(config.Arena.MaxMobs, w.MobCount,
                "fixture premise: every mob slot must be filled — TrioSaturated's "
                + "huddle is isolated from the crowd, so none of them can have died yet");
            Assert.Greater(w.ProjectileCount, 0,
                "fixture premise: warm-up must leave live projectiles in flight, not "
                + "just spawn and resolve them before the measurement below starts");
            // Fix-round 1 (M-4): ProjectileCount alone is satisfied by the duel's
            // own rounds — it does not prove player 2 (the one aiming into the
            // mob crowd) ever actually fired. ShotsFired does, cheaply.
            Assert.Greater(w.StatsAt(2).ShotsFired, 0,
                "fixture premise: player 2 must actually have fired during warm-up, "
                + "not merely be permitted to");
            for (int i = 0; i < w.PlayerCount; i++)
                Assert.IsTrue(w.PlayerAt(i).Alive, $"fixture premise: player {i} must "
                    + "survive warm-up to be measured");

            // The PvP branch Task 17 added (a Player-owned round gathering every
            // OTHER live player, ProjectileSystem.Update) must have actually
            // resolved during warm-up, not merely been reachable in principle.
            // Fix-round 1 (M-2): the reason this is unambiguous is NOT "a mob's
            // damage can't credit ShotsHit" (true, but beside the point here) —
            // it's that players 0 and 1's own rounds are consumed against EACH
            // OTHER at 3 m and never travel far enough within TrioWarmupTicks to
            // reach the mob crowd ~27 m away (see TrioSaturated's own doc), so
            // the only thing their rounds can ever hit during warm-up is each
            // other.
            int pvpShotsHit = w.StatsAt(0).ShotsHit + w.StatsAt(1).ShotsHit;
            Assert.Greater(pvpShotsHit, 0,
                "fixture premise: the point-blank duel must have landed at least "
                + "one hit during warm-up");

            // Continuing input array built OUTSIDE the measured lambda (and from
            // the world's OWN player positions, not restated literals) — a
            // `new SimInput[3]` inside the lambda below would be the test's own
            // allocation, not the world's.
            // Т4: proof that the wave director is genuinely RUNNING across the
            // window and not short-circuited by something the fixture happens
            // to do — the difficulty step is a function of the world's tick, so
            // a director that keeps starting waves keeps moving this number,
            // and a director that returned early would leave it frozen.
            int stepBefore = w.WaveRef(Zone.Outer).WaveIndex;

            float2 p0Pos = w.PlayerAt(0).Pos, p1Pos = w.PlayerAt(1).Pos;
            var inputs = new SimInput[3];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = p1Pos };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = p0Pos };
            inputs[2] = new SimInput { FireHeld = true, AimPoint = float2.zero };
            Assert.That(() =>
            {
                for (int i = 0; i < measuredTicks; i++) w.TickAll(inputs);
            }, Is.Not.AllocatingGCMemory());

            Assert.Greater(w.WaveRef(Zone.Outer).WaveIndex, stepBefore,
                "fixture premise: the wave director must have kept starting waves across the "
                + "measured window — the per-ring loop and its stackalloc are what this test "
                + "now also covers, and a frozen director would measure neither");
            for (Zone z = Zone.Middle; z <= Zone.Core; z++)
                Assert.AreEqual(0, w.WaveRef(z).PendingTotal,
                    $"fixture premise: {z} is frozen on this zoneless arena, so the freeze "
                    + "branch is what ran for it on every measured tick");

            // Fix-round 1 (I-1b): fixture-sanity doesn't stop at the FIRST tick
            // of the measured window — prove the world was still loaded on the
            // LAST one too. These sit safely after the measured lambda, so they
            // cost nothing against the allocation budget above.
            //
            // Deliberately NOT a ShotsHit-growth witness (fix-round 1 review
            // trap): once the mob crowd eventually reaches the huddle (at
            // Chaser.MaxSpeed closing the ~27 m gap) a chaser could stand ON
            // the 3 m duel line (the 2.1 m gap between the duelists' own padded
            // bodies is wider than a chaser's 1 m diameter) and win the min-t
            // scan for a round meant for the other duelist — ShotsHit growth
            // past that point no longer proves PvP by itself, it might be
            // DamageMob crediting the same counter.
            //
            // Fix-round 3 (Ф4 fix-wave, I-1b): what the Alive gate below buys
            // this measurement is exactly two claims, both readable straight
            // off ProjectileSystem.Update, and both true for the WHOLE window
            // rather than only its first tick.
            // (1) The gather's player loop is UNCONDITIONAL. `for (int pi = 0;
            //     pi < playerCount; pi++)` runs over all three players for
            //     every live projectile on every tick; the owner skip and the
            //     `player.Alive && Geometry.SegmentCircle(...)` gate decide
            //     only what lands in the scratch, never whether the loop runs.
            //     That loop, plus the min-scan over whatever it packed, IS the
            //     code under measurement — and neither has a counterpart in
            //     Tick_DoesNotAllocateGC above, whose single player is removed
            //     outright by the owner skip.
            // (2) The loop keeps being fed for the whole window. Both duelists
            //     stay Alive across it (asserted below) and their FireHeld
            //     input never lapses (the SAME inputs array is fed every one of
            //     the measuredTicks iterations above), so WeaponSystem.Update
            //     keeps spawning a fresh Player-owned round roughly every
            //     Weapon.FireInterval — of the order of measuredTicks * TickDt
            //     / Weapon.FireInterval rounds per duelist (~280 at the current
            //     balance) — each of which runs (1) again. That the HitPlayer
            //     branch genuinely resolves here, rather than rounds merely
            //     flying, is pinned by the pvpShotsHit witness above.
            // Deliberately NOT claimed, because none of it follows from the
            // code: that a round packs BOTH duelists (a round never gathers its
            // own owner, so ONE other duelist is the most any of theirs can
            // ever reach); that a player candidate is packed on every tick (the
            // muzzle sits Weapon.MuzzleOffset ahead of the shooter and the far
            // duelist's padded circle starts ~1.8 m beyond it, against a step
            // of Weapon.ProjectileSpeed * TickDt ~ 1.17 m — so the victim first
            // enters the sweep on the round's SECOND tick, and player 2 at 1.5 m
            // off the duel line is never gathered by it at all); or which
            // candidate the min-scan picks. How FULL the scratch actually gets
            // is a different question with a test of its own —
            // PvpDamageTests.SaturatedWorld_CandidateScratchDoesNotOverflow.
            for (int i = 0; i < w.PlayerCount; i++)
                Assert.IsTrue(w.PlayerAt(i).Alive,
                    $"player {i} must survive the FULL measured window, not just its first tick");
            // THIS PREMISE USED TO READ `Assert.Greater(w.ProjectileCount, 0)`, AND
            // IT WAS NEVER MEASURING WHAT IT CLAIMED (app-88jb Т4, finding Н-6).
            // ProjectileCount is an INSTANTANEOUS snapshot, and in this fixture it
            // is zero on nearly half the window: an ablation over the same 1000
            // ticks, changing one variable only (the Т4 impulse zeroed), counted
            // 449 zero ticks WITH the impulse against 437 WITHOUT it — twelve
            // ticks out of a thousand, which is noise. The premise had been a coin
            // toss since long before this epic and passed only because tick 1000
            // happened to land on a firing phase; Т4 moved the phase, it broke
            // nothing.
            //
            // WHY THE ZEROS COME IN RUNS — a stretch of five in a row was observed
            // in this window's tail. Once the crowd closes on the huddle the
            // nearest bodies stand 0.3-0.6 m from a shooter, while a fresh round
            // is born Weapon.MuzzleOffset (0.6 m) ahead of him with
            // Weapon.ProjectileRadius 0.12 against a mob Radius 0.5 — a padded
            // radius of 0.62. Its very first step therefore STARTS inside a mob's
            // circle, which is Geometry.SegmentCircle's `start inside -> t = 0`
            // branch (:26): the round is consumed on the same tick it was fired,
            // before anything reads ProjectileCount at the end of that tick. A
            // firing tick and a zero tick become indistinguishable, and the run of
            // zeros lasts exactly as long as the crowd keeps standing that close —
            // a length no code bounds, which is why "is a round in flight right
            // now" cannot be a premise here in ANY window form, however wide.
            //
            // WHAT IS MEASURED INSTEAD: that the world is still FIRING at the end
            // of the window. That is what claim (2) above actually leans on ("the
            // loop keeps being fed"), and ShotsFired is MONOTONE — it cannot be
            // unlucky about phase the way a snapshot can. One full fire interval
            // of extra ticks either moves it or proves the weapon stopped. These
            // ticks, and the array below, sit AFTER the measured lambda exactly
            // like every assertion in this block, so they cost nothing against the
            // allocation budget above.
            //
            // THE BUDGET IS ARITHMETIC, NOT A LITERAL. TicksFromSeconds(0.12) =
            // round(0.12 / TickDt) = 4, and 4 is the TIGHT bound:
            // WeaponSystem.Advance tops FireCooldown up by FireInterval after each
            // shot and takes TickDt off it every tick, so the cooldown after a shot
            // lies in (0.0867, 0.12] and reaches zero in three ticks or four. The
            // +1 is one tick of honest slack on an otherwise exact bound.
            // Ammunition cannot be what fails here: the fixture starts on
            // Weapon.AmmoStart 400 and still held 117 rounds at the end of the
            // window, so the handful of shots below never reaches the Ammo == 0
            // branch and its EmergencyFireInterval (1.25 s = 37.5 ticks) is never
            // the interval chosen.
            int fireIntervalTicks = SimulationWorld.TicksFromSeconds(config.Weapon.FireInterval) + 1;
            var shotsAtWindowEnd = new int[w.PlayerCount];
            for (int i = 0; i < w.PlayerCount; i++) shotsAtWindowEnd[i] = w.StatsAt(i).ShotsFired;
            for (int i = 0; i < fireIntervalTicks; i++) w.TickAll(inputs);
            for (int i = 0; i < w.PlayerCount; i++)
                Assert.Greater(w.StatsAt(i).ShotsFired, shotsAtWindowEnd[i],
                    $"fixture premise: player {i} must still be firing at the end of the measured "
                    + "window — all three hold FireHeld for the whole run, so a counter that stands "
                    + "still across a full fire interval means the weapon stopped, not that the "
                    + "snapshot was taken on an unlucky tick");
        }

        [Test]
        public void VisibilitySystem_Compute_DoesNotAllocateGC()
        {
            // Stage 2 Task 19 fix-round 1 (I-4): the spec's own §4 list of
            // files this phase EXTENDS requires an AllocationTests entry for
            // VisibilitySystem, and no task in the plan (Tasks 19-22) was ever
            // assigned to write it — Compute's own "zero allocations after
            // the constructor" claim (VisibilitySystem.cs's own doc) lived
            // as prose only until this test.
            //
            // Unlike Tick_DoesNotAllocateGC/SaturatedTrio above, Compute
            // only READS player/mob positions — it never fires a shot or
            // resolves damage — so no combat warm-up is needed here. The mob
            // crowd is spawned directly via the shared SpawnMobsToCap seam
            // instead of TestWorlds.Saturated's own sustained-fire warm-up:
            // nothing below ever calls Tick(), so nothing can kill a mob out
            // from under the measurement the way Saturated's own 100-tick
            // hold-fire warm-up does (a handful of the 96 routinely die to
            // player fire before Saturated even returns).
            var config = TestConfigs.Default();
            var w = new SimulationWorld(1, config);
            TestWorlds.SpawnMobsToCap(w);
            Assert.AreEqual(config.Arena.MaxMobs, w.MobCount,
                "fixture premise: every mob slot must be filled for this measurement "
                + "to exercise the FULL per-tick mob-evaluation loop, not a near-empty world");

            // Phase Ф5 fix-wave (minor): the shared TestWorlds.Capacity seam,
            // not a hand-rolled second copy of the same sum. Stage 3 Task 26
            // moved the rule itself one step further out — "MaxMobs +
            // MaxPlayers covers every entity a single Compute call can visit"
            // now lives in VisibilitySet.CapacityFor, the one home of all
            // THREE classes' caps, and TestWorlds.Capacity is a delegate to
            // it. This line named the old home and was corrected with the
            // move (Task 26 review, Minor).
            int capacity = TestWorlds.Capacity(config);
            var setA = new VisibilitySet(capacity);
            var setB = new VisibilitySet(capacity);
            // Warm-up call OUTSIDE the measured window (same discipline as
            // Tick_DoesNotAllocateGC above): the very first Compute() call
            // populates setB with real hysteresis/linger state off an empty
            // setA, so the ping-pong loop below always measures against a
            // buffer that already carries a realistic tracked-entity
            // population, not a permanently-empty `previous`.
            VisibilitySystem.Compute(w, 0, config.Visibility, setA, setB);

            VisibilitySet prev = setB, cur = setA;
            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    VisibilitySystem.Compute(w, 0, config.Visibility, prev, cur);
                    (prev, cur) = (cur, prev);
                }
            }, Is.Not.AllocatingGCMemory());

            // Fixture-liveness check AFTER the measured window (Урок 87): the
            // saturated world must still be fully loaded at the end, not
            // merely at the start.
            Assert.AreEqual(config.Arena.MaxMobs, w.MobCount,
                "fixture premise: the saturated world must still be fully loaded at "
                + "the end of the measured window too");
        }

        [Test]
        public void EventRelevance_ShouldDeliver_DoesNotAllocateGC()
        {
            // Phase Ф5 fix-wave (I-6): AllocationTests did not mention
            // EventRelevance at all, even though ShouldDeliver runs on a
            // DENSER path than Compute — once per event PER OBSERVER, against
            // Compute's once per observer per tick. Built on the same shape as
            // VisibilitySystem_Compute_DoesNotAllocateGC above: a saturated
            // world, sets warmed by a real Compute call outside the measured
            // window, then a plain loop of calls inside it.
            //
            // All four channels that can actually decide something are
            // exercised (Owner, Visible, Audible, All). The four projectile
            // kinds are deliberately NOT called: their channel is
            // DeliveryChannel.None and ShouldDeliver THROWS on them by design
            // (Task 28 owns projectile relevance), so calling them here would
            // measure exception construction rather than the delivery path.
            var config = TestConfigs.Default();
            var w = new SimulationWorld(1, config, playerCount: 3);
            TestWorlds.SpawnMobsToCap(w);
            Assert.AreEqual(config.Arena.MaxMobs, w.MobCount,
                "fixture premise: every mob slot must be filled, so the VisibilitySet the calls below "
                + "scan is a realistically long one rather than a near-empty array");
            // Observer 0 into the middle of the crowd (SpawnMobsToCap spreads
            // mobs over radii ~4…31 around the origin) — the natural
            // three-player spawn ring sits at radius 103.96 (Stage 3 Task 12;
            // 52 before it), from where very little is visible at all.
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            var setA = new VisibilitySet(TestWorlds.Capacity(config));
            var setB = new VisibilitySet(TestWorlds.Capacity(config));
            VisibilitySystem.Compute(w, 0, config.Visibility, setA, setB);

            // A subject the observer genuinely sees, taken from the set itself
            // rather than assumed: DefaultArena()'s obstacles and walls decide
            // which of the Arena.MaxMobs mobs clear LoS, and this measurement has no
            // business restating that geometry.
            int visibleMobId = 0;
            float2 visibleMobPos = float2.zero;
            for (int i = 0; i < w.MobCount && visibleMobId == 0; i++)
            {
                if (!setB.Contains(w.Mobs[i].Id)) continue;
                visibleMobId = w.Mobs[i].Id;
                visibleMobPos = w.Mobs[i].Pos;
            }
            Assert.Greater(visibleMobId, 0,
                "fixture premise: at least one mob must be visible to observer 0, or the Visible-channel "
                + "calls below would all take the refusal branch instead of the delivery one");

            // The actor of the Audible-channel event is player 1, left on its
            // own spawn-ring position and therefore NOT in observer 0's set —
            // so that event resolves through the hearing gate and the
            // quantizer, the longest path in the method.
            Assert.IsFalse(setB.Contains(VisibilityIds.ForPlayer(1)),
                "fixture premise: the audible event's actor must be invisible to observer 0, or its call "
                + "would short-circuit on the visible branch and never reach IsAudible/QuantizeAudiblePos");
            var audiblePos = new float2(config.Visibility.HearRadius - 1f, 0.7f);

            var events = new[]
            {
                new SimEvent { Kind = SimEventKind.StaminaDenied, PlayerIndex = 0, Pos = new float2(1.3f, -2.7f) },
                new SimEvent { Kind = SimEventKind.MobSpawned, MobType = MobType.Chaser,
                    EntityId = visibleMobId, Pos = visibleMobPos },
                new SimEvent { Kind = SimEventKind.PlayerDashed, PlayerIndex = 1, Pos = audiblePos },
                new SimEvent { Kind = SimEventKind.WaveStarted, Pos = new float2(4f, 4f) }
            };

            // Fixture premise, one per channel: every event below really is
            // DELIVERED to observer 0, i.e. the measured loop walks the full
            // decision path of each channel rather than bailing out early.
            for (int e = 0; e < events.Length; e++)
            {
                Assert.IsTrue(EventRelevance.ShouldDeliver(events[e], 0, 0, w, setB, config.Visibility, out _),
                    $"fixture premise: {events[e].Kind} must actually be delivered to observer 0");
            }
            Assert.IsTrue(EventRelevance.ShouldDeliver(events[2], 0, 0, w, setB, config.Visibility, out float2 heardPos));
            Assert.AreEqual(VisibilitySystem.QuantizeAudiblePos(audiblePos, config.Visibility), heardPos,
                "fixture premise: the audible event must come back COARSENED, proving it took the hearing "
                + "path and not the visible one");

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    for (int e = 0; e < events.Length; e++)
                    {
                        for (int observerIndex = 0; observerIndex < w.PlayerCount; observerIndex++)
                            EventRelevance.ShouldDeliver(events[e], observerIndex, observerIndex, w, setB, config.Visibility, out _);
                    }
                }
            }, Is.Not.AllocatingGCMemory());

            // Fixture-liveness check AFTER the measured window (Урок 87), same
            // discipline as the two measurements above: nothing in the loop
            // may have emptied the set it was scanning.
            Assert.IsTrue(setB.Contains(visibleMobId),
                "fixture premise: the visibility set must still hold its subject at the end of the "
                + "measured window too");
        }

        /// THE CLIENT'S TRACER, MEASURED AT LAST (app-88jb Т32, coordinator
        /// Ruling 294, spec §4.3). Spec §4.3 has asked for "the tracer's run
        /// does not allocate" since the phase began, and until this task the
        /// plan discharged that debt by running THIS FILE — which carried four
        /// tests and not one mention of the tracer, i.e. proved the empty set.
        /// Т32 is the task that creates the possibility in the first place: it
        /// turns a closed form into a stepped integrator that consults the
        /// arena's geometry every tick, on the render frame, so the witness is
        /// its own.
        ///
        /// ⚠ HALF OF THE SAME DEBT IS NOT THIS TASK'S. Spec §4.3 names the
        /// position HISTORY and the `Id -> slot` table in the same breath;
        /// those are Т24/Т25's and are left alone rather than swept in here.
        ///
        /// ⚠ WHAT IT ACTUALLY WATCHES, since "does not allocate" is only worth
        /// as much as the work inside the window: `ProjectileFlight.Step`,
        /// which sweeps this arena's 20 obstacles, 14 walls and 2 zone arcs
        /// once per round per tick, `ProjectileFlight.BarrierStops` behind it,
        /// and this class's own cache bookkeeping. All of them answer in
        /// structs; the arrays they read belong to the config. A green run of
        /// THIS file before `StepTo` had a body proved nothing at all, and was
        /// declared a guard rather than a witness while that was so.
        [Test]
        public void TracerStepAndWrite_DoNotAllocateGC()
        {
            // Default(), not Open(): the subject is a run against REAL geometry
            // (20 obstacles, 14 walls, 2 zone arcs). An empty disc would measure
            // the cheap half of ProjectileFlight.Step and prove little.
            SimConfig config = TestConfigs.Default();
            Assert.Greater(config.Arena.ObstacleCount + config.Arena.WallCount, 0,
                "fixture premise: with no geometry the step makes no probe at all and there "
                + "is nothing to measure");

            const int rounds = 32;
            const int spawnTick = 100;
            var tracers = new TracerProjectiles(capacity: 64, in config, catchUpBudget: 8);
            var buf = new ProjectileState[64];
            for (int i = 0; i < rounds; i++)
            {
                // ttl 100 s rather than the weapon's own: `Prune` inside the
                // measured loop must not empty the table halfway through and
                // leave it measuring an idle class. Lifetime is the caller's
                // parameter here, not a config mirror, so stating it is honest.
                Assert.IsTrue(tracers.TrySpawn(i + 1, spawnTick, new float2(0f, i * 2f), 1f,
                        math.normalize(new float2(1f, 0.05f * i)), config.Weapon.ProjectileSpeed,
                        0f, config.Weapon.ProjectileRadius, ttl: 100f),
                    $"fixture premise: round {i} was refused — the table never filled");
            }

            // Warm-up OUTSIDE the measured window: the first call to each
            // member carries JIT and other one-off work that is not this
            // class's allocation. Same discipline as the fixture-liveness
            // asserts the three tests above take before their own loops.
            tracers.StepTo(spawnTick + 1);
            Assert.Greater(tracers.WriteInto(buf, spawnTick + 1), 0,
                "fixture premise: the rounds must actually be drawn, or the loop below "
                + "measures an empty scan");

            int tick = spawnTick + 1;
            Assert.That(() =>
            {
                // Fifty frames, not a thousand: an allocation on this path shows
                // on the very first iteration, while a long run would walk every
                // round past the zone arc and end up measuring stopped tracks
                // instead of flying ones.
                for (int frame = 0; frame < 50; frame++)
                {
                    tick++;
                    tracers.StepTo(tick);
                    tracers.WriteInto(buf, tick);
                    tracers.WriteInto(buf, tick + 1);
                    tracers.Prune(tick);
                }
            }, Is.Not.AllocatingGCMemory());

            // Fixture-liveness check AFTER the measured window (Урок 87), the
            // same discipline the neighbors above keep.
            // ⚠ AND IT IS THE SHAPE OF THAT DISCIPLINE RATHER THAN A LIVE
            // GUARD, which is worth saying so the next author does not read it
            // as one (app-88jb Т32 fix-round). It cannot fail by construction:
            // `ttl: 100f` above makes `OutlivedItsEnd`'s lifetime 3000 ticks
            // and no round here has an `EndTick`, so `Prune` removes nothing
            // across a 50-tick window whatever the flight does.
            // ⛔ THE DEGENERACY THAT WOULD ACTUALLY HOLLOW THIS FIXTURE IS THE
            // ONE THE COMMENT ABOVE NAMES — "measuring stopped tracks instead
            // of flying ones" — AND NOTHING GUARDS IT. A round that meets the
            // arena's geometry is marked WAITING, and `StepTo` then reaches it
            // by `continue`: the expensive half (`ProjectileFlight.Step` over
            // 20 circles / 14 walls / 2 arcs) stops running for it entirely,
            // while `Count` stays exactly what it was. Guarding it honestly
            // needs a count of MOVING rounds, which this class has no member
            // for today; the allocation claim itself survives either way —
            // what shrinks is how much of the path the window covers.
            Assert.Greater(tracers.Count, 0,
                "fixture premise: the table must not have emptied during the measured window");
        }
    }
}
