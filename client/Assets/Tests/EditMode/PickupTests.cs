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
    /// Loot.CellsPerMob/CorpseCellFraction explicitly re-enabled per test
    /// (they are zeroed in TestConfigs itself for golden safety, owner
    /// decision R-18 — see TestConfigs.Default()'s own Loot comment; the
    /// two numbers lived on Chaser/Weapon before Т13 moved them, R-3).
    public class PickupTests
    {
        [Test]
        public void MobDeath_DropsConfiguredCells()
        {
            var cfg = TestConfigs.Open();
            cfg.Loot.CellsPerMob[(int)MobType.Chaser] = 3;
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
            cfg.Loot.CorpseCellFraction = 0.5f;

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

        /// Ф1 fix-round (review B-I-4): the UNPROVEN half of CorpseCells' own
        /// guard. `PlayerDeath_DropsHalfOfCarriedAmmo_AtLeastOne` above covers
        /// the minimum, and the golden scenarios cover `fraction <= 0` (their
        /// fixture zeroes it) — but nothing killed a player with an EMPTY
        /// magazine at a live fraction, so deleting `ammo <= 0 ||` survived the
        /// whole suite: floor(0 * 0.5 / 10) = 0 clamps up to 1, and a corpse
        /// that carried nothing would scatter a phantom cell worth ten shots.
        /// Suiciding dry would have paid.
        [Test]
        public void EmptyCorpse_DropsNothing_EvenAtALiveFraction()
        {
            var cfg = TestConfigs.Open();
            cfg.Loot.CorpseCellFraction = 0.5f;
            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player; p.Ammo = 0; w.SetPlayerForTest(p);
            Assert.AreEqual(0, w.PickupCount, "premise: nothing on the ground before the death");

            w.KillPlayerNoDamage(0);

            Assert.AreEqual(0, w.PickupCount,
                "a corpse that carried no ammo must drop nothing — the guaranteed minimum " +
                "applies to a near-dry corpse, not to an empty one");
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

        /// Ф1 fix-round (review C1 / B-I-1, owner decision R-24):
        /// `MatchStats.CellsPicked` was declared in Т1 and hashed in Т6 with
        /// no writer anywhere in `Scripts/` — a hashed field whose behavior
        /// had been postponed past the phase that was supposed to own it.
        ///
        /// THE AMOUNT 3 TELLS ALL THREE CANDIDATE UNITS APART, which is the
        /// point of choosing it: 3 means CELLS (PickupState.Amount, the unit
        /// every producer speaks — LootDrops returns cells, SpawnPickup stores
        /// them unconverted), 1 would mean piles walked over, and 30 would mean
        /// the shots those cells bought, which is AmmoSpent's unit on the other
        /// side of spec §3.10's ledger. Subject is player 1, not player 0
        /// (lesson 227), so the counter also has to land on the COLLECTOR's own
        /// slot rather than always on the first one — the same defect class
        /// Stage 2 Task 7 removed from ShotsHit/Kills.
        [Test]
        public void Collecting_CountsCellsOnTheCollectorsOwnSlot()
        {
            const int Amount = 3;
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            PlayerState p0 = w.PlayerAt(0); p0.Pos = new float2(-30f, 0f); w.SetPlayerForTest(0, p0);
            PlayerState p1 = w.PlayerAt(1); p1.Pos = new float2(10f, 0f); p1.Ammo = 50;
            w.SetPlayerForTest(1, p1);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(10f, 0f), Amount);

            w.TickAll(new SimInput[2]);

            Assert.AreEqual(0, w.PickupCount, "premise: the pile must actually have been collected");
            Assert.AreEqual(50 + Amount * cfg.Weapon.ShotsPerCell, w.PlayerAt(1).Ammo,
                "premise: …by player 1, whose magazine is the one that grew");
            Assert.AreEqual(Amount, w.StatsAt(1).CellsPicked,
                "CellsPicked counts CELLS — three of them, not one pile and not thirty shots");
            Assert.AreEqual(0, w.StatsAt(0).CellsPicked,
                "…on the collector's own slot: the bystander's counter must stay at zero");
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
            // computes zero. TestConfigs' own Loot.CellsPerMob/CorpseCellFraction
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

        /// Stage 3 Task 13 (R-3, coordinator requirement): before this task
        /// no existing test could tell PickupTtlSeconds apart from any other
        /// number — TtlExpiry_RemovesWithoutEvent above sets PickupState.Ttl
        /// directly (SetPickupForTest), bypassing SpawnPickup's own seeding
        /// entirely. A fixture whose Loot.PickupTtlSeconds differs from the
        /// removed SimulationWorld constant's old value (120) is the only
        /// way to prove SpawnPickup reads the config rather than a literal.
        [Test]
        public void SpawnPickup_SeedsTtlFromLootConfig()
        {
            var cfg = TestConfigs.Open();
            cfg.Loot.PickupTtlSeconds = 45f; // deliberately NOT 120 — see class doc
            var w = new SimulationWorld(1, cfg);

            w.SpawnPickup(PickupKind.EnergyCell, new float2(50f, 50f), 4);

            Assert.AreEqual(45f, w.Pickups[0].Ttl,
                "a freshly spawned pickup's Ttl must come from Loot.PickupTtlSeconds, not a literal");
        }

        /// Ф6-0 (errata E-6 C-I5): the ONE place the pickup TTL rule and the
        /// container TTL rule genuinely differ, and until now nothing pinned
        /// it. `ContainerStore.Update` reads `Ttl <= 0` as PERMANENT and skips
        /// the decrement (Crate/Cache/PlayerCorpse, `InitialTtlFor`); a pickup
        /// has no such reading and must simply die. Both loops now call one
        /// home, `Loot.TtlDecay.Step`, whose `zeroIsPermanent` argument is
        /// exactly that difference — so this test is what keeps the shared
        /// home from quietly handing pickups the containers' policy.
        [Test]
        public void ZeroTtl_Expires_WhereAContainerWouldBePermanent()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(50f, 50f), 4); // far from the player — never auto-collected
            PickupState p = w.Pickups[0];
            p.Ttl = 0f;
            w.SetPickupForTest(0, in p);

            w.Tick(default);

            Assert.AreEqual(0, w.PickupCount,
                "a pickup at Ttl 0 must be removed on the very next tick — 0 is a container's "
                + "\"permanent\" sentinel, never a pickup's");
        }

        // --- Stage 3 Task 16 (spec §3.7): archetype item drop on death ---

        /// Coordinator R-124/R-125: "тир предмета — тир зоны смерти" —
        /// Middle maps to tier 2, TestConfigs' own Id=1 Trophy record.
        /// DropChance is pinned to 1 for this one cell so the roll is
        /// deterministic regardless of seed (same discipline
        /// CorpseCellFraction's own tests already follow for a config that
        /// is normally zeroed for golden safety).
        [Test]
        public void EliteInMiddle_DropsTierTwo()
        {
            var cfg = TestConfigs.Open();
            cfg.Loot.DropChance[(int)MobType.Elite * 3 + (int)Zone.Middle] = 1f;
            var w = new SimulationWorld(1, cfg);
            var pos = new float2(70f, 0f); // inside {65, 92} — Middle band
            w.SpawnMobForTest(MobType.Elite, pos);

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, float2.zero, ownerIndex: 0);

            Assert.AreEqual(1, w.ContainerCount, "premise: the elite's death must have produced a container");
            ContainerState c = w.Containers[0];
            Assert.AreEqual(ContainerKind.MobCorpse, c.Kind);
            Assert.AreEqual(pos, c.Pos, "the corpse container must sit at the death position (R-129)");
            Assert.AreEqual(1, c.SlotCount);
            Assert.AreEqual(2, w.ContainerSlotAt(0, 0), "Middle => tier 2 => TestConfigs' own Id=2 record");
        }

        /// Companion to EliteInMiddle_DropsTierTwo above — Core maps to
        /// tier 3, TestConfigs' own Id=3 Trophy record. Together the pair
        /// is also the plan-mandated mutation target (Т16 Step 4): "tier
        /// from archetype instead of tier from zone" must turn THIS test
        /// red (subject = second element, lesson 227).
        [Test]
        public void EliteInCore_DropsTierThree()
        {
            var cfg = TestConfigs.Open();
            cfg.Loot.DropChance[(int)MobType.Elite * 3 + (int)Zone.Core] = 1f;
            var w = new SimulationWorld(1, cfg);
            var pos = new float2(30f, 0f); // inside {0, 65} — Core band
            w.SpawnMobForTest(MobType.Elite, pos);

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, float2.zero, ownerIndex: 0);

            Assert.AreEqual(1, w.ContainerCount, "premise: the elite's death must have produced a container");
            ContainerState c = w.Containers[0];
            Assert.AreEqual(ContainerKind.MobCorpse, c.Kind);
            Assert.AreEqual(pos, c.Pos);
            Assert.AreEqual(1, c.SlotCount);
            Assert.AreEqual(3, w.ContainerSlotAt(0, 0), "Core => tier 3 => TestConfigs' own Id=3 record");
        }

        /// Coordinator golden risk §1(а)/R-120: the archetype's own
        /// DropChance row must be checked for all-zero BEFORE
        /// Geometry.ZoneOf is ever called — ZoneOf's own ZoneRadius[0]/[1]
        /// reads (Geometry.cs:297) carry no bounds guard and throw a bare
        /// IndexOutOfRangeException on a zoneless arena (a legal input,
        /// R-53). TestConfigs.Open()'s own DropChance stays at Default()'s
        /// all-zero — this test's only addition is emptying ZoneRadius.
        [Test]
        public void ChaserDeath_OnZonelessArena_WithZeroDropChance_DoesNotThrow()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ZoneRadius = System.Array.Empty<float>(); // zoneless — legal (R-53)
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));

            Assert.DoesNotThrow(() => w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, float2.zero, ownerIndex: 0),
                "R-120: the archetype's own DropChance row must be checked BEFORE Geometry.ZoneOf " +
                "ever runs — ZoneOf's own unguarded ZoneRadius[0]/[1] reads would throw on a " +
                "zoneless arena otherwise");
            Assert.AreEqual(0, w.ContainerCount,
                "witness: golden-safety zero DropChance means no item, no container — this line " +
                "is what keeps the test from being satisfied by a stub that merely never crashes");
        }
    }
}
