using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    public class AllocationTests
    {
        [Test]
        public void Tick_DoesNotAllocateGC()
        {
            var w = TestWorlds.Saturated(out SimConfig config);
            // Sanity-check the fixture itself before measuring: every mob slot
            // must actually be filled (TestWorlds.Saturated's SpawnMobForTest
            // loop emits one MobSpawned event per mob, still buffered — nothing
            // in the 100-tick warm-up clears events).
            Assert.AreEqual(config.Arena.MaxMobs, TestEvents.CountOf(w, SimEventKind.MobSpawned));
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
            float2 p0Pos = w.PlayerAt(0).Pos, p1Pos = w.PlayerAt(1).Pos;
            var inputs = new SimInput[3];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = p1Pos };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = p0Pos };
            inputs[2] = new SimInput { FireHeld = true, AimPoint = float2.zero };
            Assert.That(() =>
            {
                for (int i = 0; i < measuredTicks; i++) w.TickAll(inputs);
            }, Is.Not.AllocatingGCMemory());

            // Fix-round 1 (I-1b): fixture-sanity doesn't stop at the FIRST tick
            // of the measured window — prove the world was still loaded on the
            // LAST one too. These sit safely after the measured lambda, so they
            // cost nothing against the allocation budget above.
            //
            // Deliberately NOT a ShotsHit-growth witness (fix-round 1 review
            // trap): once the mob crowd eventually reaches the huddle (~156
            // measured ticks in, at Chaser.MaxSpeed closing the ~27 m gap) a
            // chaser could stand ON the 3 m duel line and take a round meant for
            // the other duelist — ShotsHit growth past that point no longer
            // proves PvP by itself, it might be DamageMob crediting the same
            // counter. Continuity of the PvP branch across the WHOLE window
            // instead rests on construction, not a counter: both duelists are
            // asserted Alive below (their FireHeld input never lapses — the
            // SAME inputs array is fed every one of the measuredTicks
            // iterations above), and WeaponSystem.Update spawns a new
            // Player-owned round roughly every Weapon.FireInterval regardless of
            // whether the previous one already resolved — so
            // ProjectileSystem.Update's per-live-player gather loop keeps
            // re-entering the HitPlayer branch throughout the window by
            // construction, not by the luck of a particular tick's timing.
            for (int i = 0; i < w.PlayerCount; i++)
                Assert.IsTrue(w.PlayerAt(i).Alive,
                    $"player {i} must survive the FULL measured window, not just its first tick");
            Assert.Greater(w.ProjectileCount, 0,
                "a live projectile must still be in flight at the end of the measured window too");
        }
    }
}
