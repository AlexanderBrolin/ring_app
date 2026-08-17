using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 3 (spec §3.6): energy cells as a world entity — drop on
    /// mob/player death, automatic collection, TTL expiry, and the cap's
    /// deterministic refusal. TestConfigs.Open() is the base fixture
    /// throughout (same reasoning as AmmoTests/PredictionParityTests' own
    /// doc: waves pushed out of reach, no obstacle in the way), with
    /// CellsOnDeath/CorpseCellFraction explicitly re-enabled per test (they
    /// are zeroed in TestConfigs itself for golden safety, owner decision
    /// R-18 — see TestConfigs.Default()'s own Weapon/Chaser/Gunner comments).
    public class PickupTests
    {
        [Test]
        public void MobDeath_DropsConfiguredCells()
        {
            var cfg = TestConfigs.Open();
            cfg.Chaser.CellsOnDeath = 3;
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, float2.zero, ownerIndex: 0);

            Assert.AreEqual(1, w.PickupCount, "the mob's death must drop exactly one pickup");
            PickupState pickup = w.Pickups[0];
            Assert.AreEqual(PickupKind.EnergyCell, pickup.Kind);
            Assert.AreEqual(3, pickup.Amount);
            Assert.AreEqual(new float2(5f, 0f), pickup.Pos);
        }

        [Test]
        public void PlayerDeath_DropsHalfOfCarriedAmmo_AtLeastOne()
        {
            var cfg = TestConfigs.Open();
            cfg.Weapon.CorpseCellFraction = 0.5f;

            // General case: floor(ammo * CorpseCellFraction / ShotsPerCell).
            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player; p.Ammo = 100; w.SetPlayerForTest(p);
            w.KillPlayerNoDamage(0);
            Assert.AreEqual(1, w.PickupCount);
            Assert.AreEqual(5, w.Pickups[0].Amount, // floor(100 * 0.5 / 10) = 5
                "corpse drop must be floor(ammo * CorpseCellFraction / ShotsPerCell)");

            // Floor-clamp: a near-dry corpse still drops the guaranteed
            // minimum of one, rather than reading as "no drop happened".
            var w2 = new SimulationWorld(1, cfg);
            PlayerState p2 = w2.Player; p2.Ammo = 1; w2.SetPlayerForTest(p2);
            w2.KillPlayerNoDamage(0);
            Assert.AreEqual(1, w2.PickupCount);
            Assert.AreEqual(1, w2.Pickups[0].Amount, // floor(1 * 0.5 / 10) = 0, clamped up to 1
                "a corpse with ANY ammo must drop at least one cell");
        }

        [Test]
        public void WalkingOver_PicksUpAndAddsAmmo()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player; p.Pos = new float2(10f, 0f); p.Ammo = 50; w.SetPlayerForTest(p);
            int pickupAmount = 4;
            w.SpawnPickup(PickupKind.EnergyCell, new float2(11f, 0f), pickupAmount); // 1 m away, inside PickupRadius (2 m)

            w.Tick(default);

            Assert.AreEqual(0, w.PickupCount, "the pickup must be collected off the ground");
            Assert.AreEqual(50 + pickupAmount * cfg.Weapon.ShotsPerCell, w.Player.Ammo);
        }

        [Test]
        public void TwoPlayersOnOneCell_LowerIndexWins()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            PlayerState p0 = w.PlayerAt(0); p0.Pos = new float2(10f, 0f); p0.Ammo = 20; w.SetPlayerForTest(0, p0);
            PlayerState p1 = w.PlayerAt(1); p1.Pos = new float2(10.5f, 0f); p1.Ammo = 20; w.SetPlayerForTest(1, p1);
            int pickupAmount = 2;
            w.SpawnPickup(PickupKind.EnergyCell, new float2(10.2f, 0f), pickupAmount); // within radius of both

            w.TickAll(new SimInput[2]);

            Assert.AreEqual(0, w.PickupCount, "the contested cell is gone after the tick either way");
            Assert.AreEqual(20 + pickupAmount * cfg.Weapon.ShotsPerCell, w.PlayerAt(0).Ammo,
                "the lower player index must win the contested cell");
            Assert.AreEqual(20, w.PlayerAt(1).Ammo,
                "the higher index must get nothing — the cell was already gone by its turn");
        }

        [Test]
        public void DeadInSameTick_PicksNothing()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player; p.Pos = new float2(10f, 0f); p.Ammo = 20; w.SetPlayerForTest(p);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(10f, 0f), 3);

            // The player dies before PickupSystem's own guard can see them
            // alive — KillPlayerNoDamage sets Alive=false exactly as combat
            // would have, and PickupSystem (the LAST step of TickAll, spec
            // §3.6) has no way to distinguish "died moments before this
            // Tick()" from "died during this Tick()'s own combat phase" —
            // both read Alive == false at the one point the guard checks it.
            w.KillPlayerNoDamage(0);
            w.Tick(default);

            Assert.AreEqual(1, w.PickupCount, "a dead player must not collect the pickup");
            Assert.AreEqual(20, w.Player.Ammo, "…and must not gain ammo from it either");
        }

        [Test]
        public void ExtractedInSameTick_PicksNothing()
        {
            // Errata E-6 D-I3: the mutation for the "not extracted" guard
            // targets THIS test, not DeadInSameTick above — Extracted has no
            // other test proving its own half of the guard at all (removing
            // just the `!player.Extracted` half of the guard leaves
            // DeadInSameTick green, since that fixture never sets Extracted).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player;
            p.Pos = new float2(10f, 0f); p.Ammo = 20; p.Alive = true; p.Extracted = true;
            w.SetPlayerForTest(p);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(10f, 0f), 3);

            w.Tick(default);

            Assert.AreEqual(1, w.PickupCount, "an extracted player must not collect the pickup");
            Assert.AreEqual(20, w.Player.Ammo, "…and must not gain ammo from it either");
        }

        [Test]
        public void CapReached_SkipsAndCounts()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.MaxPickups = 1; // lesson 227: the SECOND pickup is the subject, not the first
            var w = new SimulationWorld(1, cfg);

            int firstId = w.SpawnPickup(PickupKind.EnergyCell, new float2(1f, 0f), 5);
            Assert.AreNotEqual(-1, firstId, "premise: the first drop must actually fill the one slot");
            Assert.AreEqual(0, w.WorldStats.PickupSpawnsSkipped);

            int secondId = w.SpawnPickup(PickupKind.EnergyCell, new float2(2f, 0f), 7);

            Assert.AreEqual(-1, secondId, "the second drop must be refused once the cap is full");
            Assert.AreEqual(1, w.WorldStats.PickupSpawnsSkipped);
            Assert.AreEqual(1, w.PickupCount, "the FIRST pickup must not be evicted to make room");
            Assert.AreEqual(5, w.Pickups[0].Amount, "…and it must still be the original one, untouched");
        }

        [Test]
        public void TtlExpiry_RemovesWithoutEvent()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(50f, 50f), 4); // far from the player — never auto-collected
            PickupState p = w.Pickups[0];
            p.Ttl = SimulationWorld.TickDt * 1.5f; // expires on the SECOND tick
            w.SetPickupForTest(0, in p);

            w.Tick(default);
            Assert.AreEqual(1, w.PickupCount, "premise: must not have expired on the first tick yet");
            w.ClearEvents();

            w.Tick(default);
            Assert.AreEqual(0, w.PickupCount, "the pickup must be gone once its TTL crosses zero");
            Assert.AreEqual(0, w.EventCount, "expiry must not emit any event");
        }

        [Test]
        public void ZeroAmountDrop_SpawnsNothing_DoesNotConsumeEntityId()
        {
            // Owner decision R-18: a zero-amount drop must be skipped
            // WHOLESALE — not spawn a PickupState with Amount = 0 — or
            // _nextEntityId still advances and the golden hash moves outside
            // a sanctioned re-pin the moment a caller's drop math legitimately
            // computes zero. TestConfigs' own CellsOnDeath/CorpseCellFraction
            // = 0 makes EVERY drop in the golden scenarios exactly this case,
            // which is the whole premise this task stays golden-safe without
            // either sanctioned re-pin (Т6/Т12).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int before = w.PickupCount;

            int idBeforeGap = w.SpawnMobForTest(MobType.Chaser, float2.zero);
            int refused = w.SpawnPickup(PickupKind.EnergyCell, float2.zero, 0);
            int idAfterGap = w.SpawnMobForTest(MobType.Chaser, float2.zero);

            Assert.AreEqual(-1, refused, "a zero-amount drop must report no entity");
            Assert.AreEqual(before, w.PickupCount, "no slot was filled");
            Assert.AreEqual(0, w.WorldStats.PickupSpawnsSkipped,
                "a zero-amount drop is not a cap overflow — the skip counter must not move either");
            Assert.AreEqual(idBeforeGap + 1, idAfterGap,
                "_nextEntityId must not have advanced between the two mob spawns — the " +
                "zero-amount drop between them must not have consumed an id");
        }
    }
}
