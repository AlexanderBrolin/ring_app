using NUnit.Framework;
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
            Assert.Greater(w.ProjectileCount, 0,
                "a live projectile must still be in flight at the end of the measured window too");
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
    }
}
