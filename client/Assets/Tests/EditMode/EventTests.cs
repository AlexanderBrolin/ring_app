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
    }
}
