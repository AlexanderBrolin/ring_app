using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class EventTests
    {
        [Test]
        public void Emit_RecordsKindTickAndPayload()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Tick(default); // tick = 1
            w.Emit(SimEventKind.PlayerDashed, new float2(1f, 2f), 0, default, 0f);
            Assert.AreEqual(1, w.EventCount);
            SimEvent e = w.GetEvent(0);
            Assert.AreEqual(SimEventKind.PlayerDashed, e.Kind);
            Assert.AreEqual(1, e.Tick);
            Assert.AreEqual(new float2(1f, 2f), e.Pos);
        }

        [Test]
        public void Emit_WithoutZoneArgs_DefaultsToNoneAndZeroDirection()
        {
            // Task 6 added zone/hitDir as OPTIONAL parameters precisely so the
            // existing five-argument call sites stay untouched — this pins the
            // neutral payload they keep producing.
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Emit(SimEventKind.WaveStarted, float2.zero, 3, default, 0f);
            SimEvent e = w.GetEvent(0);
            Assert.AreEqual(HitZone.None, e.Zone);
            Assert.AreEqual(float2.zero, e.HitDir);
        }

        [Test]
        public void Emit_CarriesZoneAndHitDir()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Emit(SimEventKind.ProjectileHit, float2.zero, 7, MobType.Gunner, 20.4f,
                zone: HitZone.Head, hitDir: new float2(0f, 1f));
            SimEvent e = w.GetEvent(0);
            Assert.AreEqual(HitZone.Head, e.Zone);
            Assert.AreEqual(new float2(0f, 1f), e.HitDir);
            Assert.AreEqual(20.4f, e.Amount, 1e-4f);
        }

        [Test]
        public void ClearEvents_ResetsCount()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Emit(SimEventKind.WaveStarted, float2.zero, 1, default, 0f);
            w.ClearEvents();
            Assert.AreEqual(0, w.EventCount);
        }

        [Test]
        public void Overflow_DropsDeterministicallyWithoutGrowth()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(1, cfg);
            int cap = cfg.Arena.MaxEventsPerFrame;
            for (int i = 0; i < cap + 10; i++)
                w.Emit(SimEventKind.ProjectileFired, float2.zero, i, default, 0f);
            Assert.AreEqual(cap, w.EventCount);
            Assert.AreEqual(10, w.DroppedEvents);
        }

        [Test]
        public void ProjectileFired_CarriesOwner_PlayerAndMob()
        {
            // F-3 regression: SimEvent now threads ProjectileOwner through
            // (SimulationWorld.SpawnProjectile) so Presentation can tell a mob's
            // shot from the player's own — before this field existed, a Gunner's
            // shot spawned the player's own shell casing, played the player's
            // muzzle sound, and could steal the player's predicted-shot latch (bd
            // app-ai2). Spawns through the real production paths (WeaponSystem for
            // the player, MobAiSystem for a Gunner) rather than the raw
            // SpawnProjectileForTest seam, so this pins the actual call sites, not
            // just the Emit plumbing.
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.Tick(new SimInput { AimPoint = new float2(10f, 0f), FireHeld = true }); // player's first shot is instant

            SimEvent playerShot = default;
            bool foundPlayer = false;
            for (int i = 0; i < w.EventCount; i++)
            {
                if (w.GetEvent(i).Kind != SimEventKind.ProjectileFired) continue;
                playerShot = w.GetEvent(i);
                foundPlayer = true;
                break;
            }
            Assert.IsTrue(foundPlayer);
            Assert.AreEqual(ProjectileOwner.Player, playerShot.Owner);

            // A Gunner well inside PreferredRange+-RangeTolerance with clear LoS
            // fires on its first eligible tick (F-1's own fix keeps this to exactly
            // one shot — see MobAiTests.Gunner_LongApproach_FiresAtMostOnceOnFirstWindow).
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f));
            SimEvent mobShot = default;
            bool foundMob = false;
            for (int i = 0; i < 60 && !foundMob; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                {
                    if (w.GetEvent(e).Kind != SimEventKind.ProjectileFired) continue;
                    mobShot = w.GetEvent(e);
                    foundMob = true;
                    break;
                }
            }
            Assert.IsTrue(foundMob);
            Assert.AreEqual(ProjectileOwner.Mob, mobShot.Owner);
        }

        [Test]
        public void PlayerEvents_CarryPlayerIndex()
        {
            // Stage 2 Task 7: SimEvent.PlayerIndex threads through
            // SimulationWorld's production call sites — TickMovement
            // (PlayerDashed/StaminaDenied here; PlayerSlideStarted/
            // DashRicocheted share the exact same `playerIndex: (byte)i`
            // argument at the same call site inside the same per-player loop,
            // so this pins the pattern for all four "own-action" movement
            // kinds), WeaponSystem -> SpawnProjectile (ProjectileFired), and
            // DamagePlayer (PlayerDamaged/PlayerDied carry the VICTIM's index,
            // spec §3.2 — same convention as EntityId for those two kinds).
            // Player 1 (not player 0) acts, so a hardcoded-to-0 stub fails
            // this the same way ProjectileFired_CarriesOwner_PlayerAndMob
            // above pins Owner against a hardcoded-to-Player stub.
            var cfg = TestConfigs.Open();

            // PlayerDashed: player 1 dashes, player 0 stays idle.
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            var dashInputs = new SimInput[2];
            dashInputs[1] = new SimInput { DashRequested = true, MoveDir = new float2(1f, 0f) };
            w.TickAll(dashInputs);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDashed, out SimEvent dashed));
            Assert.AreEqual(1, dashed.PlayerIndex, "player 1 dashed — the event must carry THEIR index");

            // ProjectileFired: player 1 fires (fresh FireCooldown — instant first shot,
            // same "player's first shot is instant" fact ProjectileFired_CarriesOwner_
            // PlayerAndMob above relies on).
            var w2 = new SimulationWorld(1, cfg, playerCount: 2);
            var fireInputs = new SimInput[2];
            fireInputs[1] = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            w2.TickAll(fireInputs);
            Assert.IsTrue(TestEvents.TryFirstOf(w2, SimEventKind.ProjectileFired, out SimEvent fired));
            Assert.AreEqual(1, fired.PlayerIndex, "player 1 fired — the event must carry THEIR index");

            // StaminaDenied: player 1's Stamina forced below DashStaminaCost via the
            // test seam — a fresh world so no earlier dash's cooldown interferes.
            var w3 = new SimulationWorld(1, cfg, playerCount: 2);
            PlayerState p1 = w3.PlayerAt(1);
            p1.Stamina = 0f;
            w3.SetPlayerForTest(1, p1);
            var denyInputs = new SimInput[2];
            denyInputs[1] = new SimInput { DashRequested = true, MoveDir = new float2(1f, 0f) };
            w3.TickAll(denyInputs);
            Assert.IsTrue(TestEvents.TryFirstOf(w3, SimEventKind.StaminaDenied, out SimEvent denied));
            Assert.AreEqual(1, denied.PlayerIndex, "player 1's denial — the event must carry THEIR index");

            // PlayerDamaged/PlayerDied carry the VICTIM's index — today that is
            // always player 0 (DamagePlayer's local `victim` stays hardcoded until
            // Task 17's PvP routing), so this pins today's byte-for-byte behaviour,
            // not multi-victim routing.
            var w4 = new SimulationWorld(1, cfg, playerCount: 2);
            w4.KillPlayerForTest();
            Assert.IsTrue(TestEvents.TryFirstOf(w4, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.AreEqual(0, damaged.PlayerIndex);
            Assert.IsTrue(TestEvents.TryFirstOf(w4, SimEventKind.PlayerDied, out SimEvent died));
            Assert.AreEqual(0, died.PlayerIndex);
        }
    }
}
