using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 14 (spec §3.7, Р229): the container as ONE entity type —
    /// spawn/take/read through SimulationWorld, TTL policy through
    /// Loot.ContainerStore. TestConfigs.Open() throughout (same reasoning as
    /// PickupTests' own doc: no obstacle/wave interference); nothing here
    /// touches StateHash directly — that coverage lives in
    /// WorldLifecycleTests (the reflective ContainerState sweep and
    /// ContainerState_IsHashedAndRestoredWithTheSave), next to every other
    /// entity's own hash proof.
    public class LootContainerTests
    {
        [Test]
        public void SpawnedContainer_HoldsGivenItems()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            int id = w.SpawnContainer(ContainerKind.Crate, new float2(4f, 0f), new byte[] { 5, 9 });

            Assert.AreNotEqual(-1, id, "premise: the container must actually be created");
            Assert.AreEqual(1, w.ContainerCount);
            ContainerState c = w.Containers[0];
            Assert.AreEqual(ContainerKind.Crate, c.Kind);
            Assert.AreEqual(new float2(4f, 0f), c.Pos);
            Assert.AreEqual(2, c.SlotCount);
            // Reads by the container's own Id (1, since a fresh world's
            // _nextEntityId starts at 1) while the container itself lives
            // at array POSITION 0 — an implementation that (incorrectly)
            // used containerId as the slot-block index directly could not
            // pass this by coincidence.
            Assert.IsTrue(w.TryTakeFromContainer(id, 0, out byte item0));
            Assert.AreEqual(5, item0);
            Assert.IsTrue(w.TryTakeFromContainer(id, 1, out byte item1));
            Assert.AreEqual(9, item1);
        }

        /// Р229: swap-remove must carry the slot BLOCK along with the
        /// struct. Lesson 227 — the SECOND container is the subject: it is
        /// the one that gets relocated into the removed slot's position,
        /// so it is the one whose content can be silently swapped for its
        /// predecessor's stale leftover if the block copy is missing.
        [Test]
        public void SwapRemove_DoesNotTransferSlotsToNeighbour()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });
            int secondId = w.SpawnContainer(ContainerKind.Crate, new float2(2f, 0f), new byte[] { 9 });
            Assert.AreEqual(2, w.ContainerCount, "premise: both containers must exist before the removal");

            w.RemoveContainerAt(0); // removes the FIRST container; the second swaps into slot 0

            Assert.AreEqual(1, w.ContainerCount);
            Assert.AreEqual(secondId, w.Containers[0].Id,
                "premise: the second container now occupies array position 0");
            Assert.IsTrue(w.TryTakeFromContainer(secondId, 0, out byte item),
                "the second container's own slot block must have moved WITH it");
            Assert.AreEqual(9, item,
                "…and must still hold ITS OWN item, not the first container's stale leftover");
        }

        [Test]
        public void TakeFromEmptySlot_ReturnsFalse()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });

            Assert.IsTrue(w.TryTakeFromContainer(id, 0, out byte first), "premise: the first take must succeed");
            Assert.AreEqual(5, first);

            bool ok = w.TryTakeFromContainer(id, 0, out byte second);

            Assert.IsFalse(ok, "a slot already taken must read as empty on a second take");
            Assert.AreEqual(0, second, "an empty slot's item must be reported as 0 (спека: 0 = пусто)");
        }

        [Test]
        public void CapReached_SkipsAndCounts()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.MaxContainers = 1; // lesson 227: the SECOND container is the subject, not the first
            var w = new SimulationWorld(1, cfg);

            int firstId = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });
            Assert.AreNotEqual(-1, firstId, "premise: the first container must actually fill the one slot");
            Assert.AreEqual(0, w.WorldStats.ContainerSpawnsSkipped);

            int secondId = w.SpawnContainer(ContainerKind.Crate, new float2(2f, 0f), new byte[] { 9 });

            Assert.AreEqual(-1, secondId, "the second container must be refused once the cap is full");
            Assert.AreEqual(1, w.WorldStats.ContainerSpawnsSkipped);
            Assert.AreEqual(1, w.ContainerCount, "the FIRST container must not be evicted to make room");
            Assert.AreEqual(5, w.ContainerSlotAt(0, 0), "…and it must still be the original one, untouched");
        }

        /// Interfaces doc, dословно: "0 = не истекает (ящик, тайник, труп
        /// сборщика)" — Crate and PlayerCorpse are two of the three
        /// permanent kinds (Cache is the third, sharing the same branch,
        /// no test of its own needed — same code path).
        [Test]
        public void TtlZero_NeverExpires_ForCrateAndPlayerCorpse()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnContainer(ContainerKind.Crate, new float2(50f, 50f), new byte[] { 5 });
            w.SpawnContainer(ContainerKind.PlayerCorpse, new float2(60f, 60f), new byte[] { 9 });
            Assert.AreEqual(2, w.ContainerCount, "premise: both containers must exist");

            // Well past Loot.ContainerTtlSeconds — proves "permanent" rather
            // than merely "hasn't expired yet".
            int ticks = (int)math.ceil(cfg.Loot.ContainerTtlSeconds / SimulationWorld.TickDt) + 5;
            for (int i = 0; i < ticks; i++) w.Tick(default);

            Assert.AreEqual(2, w.ContainerCount, "Crate/PlayerCorpse containers must never expire");
        }

        [Test]
        public void TtlExpiry_RemovesGroundContainer()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnContainer(ContainerKind.Ground, new float2(50f, 50f), new byte[] { 5 });
            Assert.AreEqual(1, w.ContainerCount, "premise: the container must actually exist");
            ContainerState c = w.Containers[0];
            c.Ttl = SimulationWorld.TickDt * 1.5f; // expires on the SECOND tick, same idiom as PickupTests
            w.SetContainerForTest(0, in c);

            w.Tick(default);
            Assert.AreEqual(1, w.ContainerCount, "premise: must not have expired on the first tick yet");

            w.Tick(default);
            Assert.AreEqual(0, w.ContainerCount, "the container must be gone once its TTL crosses zero");
        }

        /// Coordinator R-99: a block-overflowing spawn must refuse by name,
        /// not write past its own reserved slot block — an unguarded write
        /// would corrupt the NEXT container's own slots on a flat array,
        /// with neither an exception nor any other observable sign at the
        /// call site (the mirror-image of the defect swap-remove's own
        /// block copy exists to prevent, Р229).
        [Test]
        public void ItemsExceedSlotCapacity_ThrowsNamedRefusal()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var tooMany = new byte[cfg.Arena.MaxContainerSlots + 1];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = (byte)(i + 1);

            var ex = Assert.Throws<System.ArgumentException>(
                () => w.SpawnContainer(ContainerKind.Crate, float2.zero, tooMany));

            Assert.That(ex.Message, Does.Contain("MaxContainerSlots"));
        }

        // --- Stage 3 Task 15: PlaceStartingContainers (spec §3.7, Р262) ---
        //
        // Loot.ContainerStore.PlaceStartingContainers is called ONCE, from
        // SimulationWorld's own constructor, after every entity array
        // exists and after player depenetration — every test below
        // therefore exercises it simply by constructing a SimulationWorld
        // with non-zero Loot counts, not by calling it directly.

        /// Coordinator R-105/R-50: two worlds on the same seed, same
        /// non-zero counts, must place BYTE-IDENTICAL containers (Kind +
        /// Pos, same order) — the standard determinism proof, applied to
        /// the third RNG stream's own consumer for the first time.
        [Test]
        public void SameSeed_SamePlacement()
        {
            var cfg = TestConfigs.Default();
            cfg.Loot.CrateCount = 3;
            cfg.Loot.CacheCountMiddle = 2;
            cfg.Loot.CacheCountCore = 1;

            var w1 = new SimulationWorld(777, cfg);
            var w2 = new SimulationWorld(777, cfg);

            Assert.Greater(w1.ContainerCount, 0, "premise: something must actually be placed to compare");
            Assert.AreEqual(w1.ContainerCount, w2.ContainerCount);
            for (int i = 0; i < w1.ContainerCount; i++)
            {
                Assert.AreEqual(w1.Containers[i].Kind, w2.Containers[i].Kind, $"container {i} kind");
                Assert.AreEqual(w1.Containers[i].Pos, w2.Containers[i].Pos, $"container {i} pos");
            }
        }

        /// Coordinator §5 (the ONLY thing that makes Р230's third RNG
        /// stream a rule rather than a declaration): two worlds on the same
        /// seed, differing ONLY in CrateCount, must produce an IDENTICAL
        /// sequence of WaveSystem's own MobSpawned events, tick by tick.
        /// Comparing StateHash is illegal here (containers enter the
        /// digest, the two hashes diverge legitimately) — the wave event
        /// sequence is compared by hand instead, since TestEvents only
        /// offers CountOf/TryFirstOf, not a full ordered walk.
        /// Mutation witness named in advance (coordinator §5): placement
        /// drawing from _waveRng instead of _lootRng must turn this red.
        [Test]
        public void ChangingCrateCount_DoesNotMoveWaveSpawns()
        {
            var cfgA = TestConfigs.Default(); // CrateCount 0 — this task's own golden-safety baseline
            var cfgB = TestConfigs.Default();
            cfgB.Loot.CrateCount = 5;

            var wA = new SimulationWorld(42, cfgA);
            var wB = new SimulationWorld(42, cfgB);

            Assert.AreEqual(0, wA.ContainerCount, "premise: the baseline world places nothing");
            Assert.Greater(wB.ContainerCount, 0,
                "premise: the modified world must have actually drawn from _lootRng to place " +
                "containers — otherwise this test cannot tell isolation from indifference");

            const int ticks = 300; // comfortably past FirstWaveDelay (2.5s = 75 ticks) into Active
            for (int t = 0; t < ticks; t++)
            {
                wA.Tick(default);
                wB.Tick(default);

                int idxA = 0, idxB = 0;
                while (true)
                {
                    while (idxA < wA.EventCount && wA.GetEvent(idxA).Kind != SimEventKind.MobSpawned) idxA++;
                    while (idxB < wB.EventCount && wB.GetEvent(idxB).Kind != SimEventKind.MobSpawned) idxB++;
                    bool hasA = idxA < wA.EventCount;
                    bool hasB = idxB < wB.EventCount;
                    Assert.AreEqual(hasA, hasB, $"tick {t}: MobSpawned counts diverged after changing CrateCount");
                    if (!hasA) break;
                    Assert.AreEqual(wA.GetEvent(idxA).Pos, wB.GetEvent(idxB).Pos,
                        $"tick {t}: a wave spawn position diverged after changing ONLY CrateCount");
                    idxA++;
                    idxB++;
                }
                wA.ClearEvents();
                wB.ClearEvents();
            }
        }

        /// Coordinator: termination by the SAME construction as a fully-
        /// blocked wave ring (spec §3.13 item 5) — a single obstacle
        /// centered at the ARENA'S OWN origin blocks the Outer spawn ring
        /// at every angle at once (CircleOverlap's distance test reduces to
        /// a pure radius comparison when both centers coincide), so this
        /// does not depend on RNG luck at all: candidates AND the RNG-free
        /// grid are equally blocked, every attempt is refused, and the
        /// world still finishes constructing instead of hanging.
        [Test]
        public void BlockedArena_TerminatesAndCounts()
        {
            var cfg = TestConfigs.Open();
            float outerRing = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { float2.zero };
            cfg.Arena.ObstacleRadius = new[] { outerRing + 5f }; // covers the WHOLE ring, any angle
            cfg.Loot.CrateCount = 3;

            var w = new SimulationWorld(1, cfg);

            Assert.AreEqual(0, w.ContainerCount, "the whole Outer spawn ring is blocked — no crate can land");
            Assert.AreEqual(3, w.WorldStats.ContainerSpawnsSkipped,
                "witness: the search must have actually RUN and failed 3 times (not merely never " +
                "been attempted) — every one of the 3 requested crates must be counted");
        }

        /// Coordinator R-106 (the taskʼs central judgement call): a
        /// container is rejected by Geometry.InArcBand, NOT OverlapsArc —
        /// unlike a mob, it must not be allowed to sit inside a doorway.
        /// The zone wall is laid EXACTLY on the Middle zone's own spawn
        /// ring (same construction idiom as ZoneConfigTests.cs, the
        /// "arc across the fallback ring locks it" fixture), WITH a real
        /// door — under InArcBand the door buys nothing, because InArcBand
        /// is a pure RADIAL test with no angular door exception at all, so
        /// every point on this ring — inside the door span included — is
        /// still on the wrong side of the radial band.
        [Test]
        public void NoContainerInsideArcOrDoor()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ZoneRadius = new[] { 20f, 40f };
            cfg.Arena.ZoneWallCount = 1;
            float wallRadius = 40f - cfg.Wave.SpawnRingInset; // exactly Middle's own spawn ring (R-105)
            cfg.Arena.ZoneWallRadius = new[] { wallRadius };
            cfg.Arena.ZoneWallHalfWidth = new[] { 1f };
            cfg.Arena.ZoneWallDoorStart = new[] { 0 };
            cfg.Arena.ZoneWallDoorCount = new[] { 1 };
            cfg.Arena.DoorCenterRad = new[] { 0f };
            cfg.Arena.DoorFreeWidth = new[] { 6f };
            cfg.Loot.CacheCountMiddle = 3;

            var w = new SimulationWorld(1, cfg);

            Assert.AreEqual(0, w.ContainerCount,
                "the whole Middle spawn ring sits on the wall band, door span included — InArcBand " +
                "(unlike OverlapsArc) must reject a candidate there regardless of angle");
            Assert.AreEqual(3, w.WorldStats.ContainerSpawnsSkipped,
                "witness: the search must have actually RUN and failed 3 times, not merely never " +
                "been attempted");
        }

        /// Errata E-6 D-I5 (coordinator §4/§6.1): the one test in this file
        /// that proves the RNG-free FALLBACK GRID itself works, not just
        /// that the candidate search does. A single obstacle centered at
        /// the ring's own ANTIPODE (angle PI) blocks every point on the
        /// ring except a narrow window right around angle 0 — CircleOverlap
        /// against an origin-symmetric pair of ring points reduces to a
        /// pure chord-length/angle relationship (`2*ringRadius*sin(Δ/2)`),
        /// inverted below to size the obstacle exactly. The assertion is
        /// MECHANISM-based, not geometry-based (coordinator R-113.2): the
        /// crate's position must be EXACTLY EQUAL to fallback grid slot
        /// #0's own SpawnPlacement.FallbackSlotPos value — proving the grid
        /// found it, independent of which 16 angles the candidate loop drew.
        /// ⚠ Residual risk (documented, not hidden): a random candidate
        /// COULD land inside the narrow open window before the grid is ever
        /// reached — closed by picking a DIFFERENT seed on the next run if
        /// that happens (coordinator R-113.2 — never by weakening the assert).
        [Test]
        public void RandomCandidate_Blocked_FallbackGridFinds()
        {
            var cfg = TestConfigs.Open();
            float outerRing = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            int slots = cfg.Loot.LootFallbackSlots;
            float heroRadius = cfg.Hero.Radius;
            // Narrowed to a ~1.1 deg window AND a 3-attempt budget (both
            // fixture-only, not a production number) to keep the residual
            // RNG-luck risk small: a continuous draw landing inside a ~0.3%
            // arc on any ONE of 3 attempts is a low-probability event, not
            // a certainty either way -- if THIS run shows a random
            // candidate found it instead of the grid, the fixture (seed
            // and/or window) gets adjusted, per coordinator R-113.2, not
            // the assertion.
            const float halfWindowRad = 0.02f; // ~1.15° open window, centered on grid slot #0 (angle 0)
            cfg.Loot.LootSpawnAttempts = 3;
            float obstacleRadius = 2f * outerRing * math.cos(halfWindowRad) - heroRadius;
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(-outerRing, 0f) }; // ring position at angle PI
            cfg.Arena.ObstacleRadius = new[] { obstacleRadius };
            cfg.Loot.CrateCount = 1;

            var w = new SimulationWorld(9001, cfg); // seed picked for this fixture — re-pick if unlucky

            Assert.AreEqual(1, w.ContainerCount, "premise: the crate must have been placed SOMEHOW");
            float2 gridSlotZeroPos = SpawnPlacement.FallbackSlotPos(outerRing, 0, slots);
            Assert.AreEqual(gridSlotZeroPos, w.Containers[0].Pos,
                "the crate must sit EXACTLY on fallback grid slot #0's own computed position — " +
                "proving the RNG-free grid, not a lucky random candidate, found it");
        }

        /// Paired with RandomCandidate_Blocked_FallbackGridFinds above
        /// (coordinator R-113.2): the OPPOSITE branch, made independently
        /// observable. A wide-open ring (no obstacles at all) — the
        /// candidate loop finds a spot on its very first draw almost
        /// certainly, and that position must NOT coincide with any of the
        /// grid's own fixed angles (a continuous uniform draw landing
        /// EXACTLY on one of finitely many fixed points is a measure-zero
        /// event). Without this test, "the grid found it" (above) is
        /// proven but "the candidates find it too, not always the grid" is
        /// not — R-113.2's own requirement that both branches be observable.
        [Test]
        public void RandomCandidate_OnOpenArc_DoesNotLandOnGridSlot()
        {
            var cfg = TestConfigs.Open();
            float outerRing = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            int slots = cfg.Loot.LootFallbackSlots;
            cfg.Loot.CrateCount = 1;

            var w = new SimulationWorld(1, cfg);

            Assert.AreEqual(1, w.ContainerCount, "premise: the crate must have been placed");
            bool matchesAnyGridSlot = false;
            for (int i = 0; i < slots; i++)
            {
                float2 slotPos = SpawnPlacement.FallbackSlotPos(outerRing, i, slots);
                if (w.Containers[0].Pos.Equals(slotPos)) { matchesAnyGridSlot = true; break; }
            }
            Assert.IsFalse(matchesAnyGridSlot,
                "an unblocked candidate draw must not land EXACTLY on a fixed grid slot — a " +
                "measure-zero coincidence for a continuous RNG draw");
        }

        /// My own witness (no name given by the brief/coordinator) for
        /// "the candidate loop actually draws from _lootRng before ever
        /// reaching the RNG-free grid" — WorldLifecycleTests.
        /// LootRng_IsItsOwnStream_HashedAndSaved proves the STREAM exists
        /// and is hashed/saved, not that placement draws from it; this
        /// proves the draw itself happens. Compares RNG STATE, not
        /// position, so it is immune to the RNG-luck risk the two tests
        /// above carry.
        [Test]
        public void PlacementConsumesLootRng_WhenNotFullyBlocked()
        {
            var cfgEmpty = TestConfigs.Default(); // CrateCount 0 — draws nothing
            var cfgFull = TestConfigs.Default();
            cfgFull.Loot.CrateCount = 1;

            var wEmpty = new SimulationWorld(9, cfgEmpty);
            var wFull = new SimulationWorld(9, cfgFull);

            Assert.AreEqual(1, wFull.ContainerCount, "premise: the container must have actually been placed");
            Assert.AreNotEqual(wEmpty.LootRng.state, wFull.LootRng.state,
                "the candidate loop must draw from _lootRng before ever reaching the RNG-free grid");
        }

        /// My own witness for "the container's own body radius is
        /// Hero.Radius, not 0" (coordinator R-104) — angle-independent,
        /// same origin-symmetric obstacle trick as BlockedArena_
        /// TerminatesAndCounts above, sized to a RAZOR'S EDGE instead of a
        /// wide margin: `ringRadius < R_obs + radius` is true for every
        /// point on the ring at radius = Hero.Radius (0.45) and false for
        /// every point at radius = 0 — a single obstacle either blocks the
        /// WHOLE ring or NONE of it, with the radius argument alone
        /// deciding which. No RNG-luck involved.
        [Test]
        public void ContainerBodyRadius_UsesHeroRadius_NotZero()
        {
            var cfg = TestConfigs.Open();
            float outerRing = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { float2.zero };
            cfg.Arena.ObstacleRadius = new[] { outerRing - 0.2f }; // razor's edge — see doc above
            cfg.Loot.CrateCount = 1;

            var w = new SimulationWorld(1, cfg);

            Assert.AreEqual(0, w.ContainerCount,
                "at radius=Hero.Radius (0.45) the obstacle blocks the WHOLE ring — a radius=0 " +
                "mutant would NOT be blocked and the crate would land");
            Assert.AreEqual(1, w.WorldStats.ContainerSpawnsSkipped,
                "witness: the search must have actually RUN and failed once, not merely never " +
                "been attempted");
        }

        /// My own witness for "a container refuses to overlap another
        /// container" (coordinator: "контейнерный фильтр добавляет своё:
        /// перекрытие с ДРУГИМ контейнером") — a PIGEONHOLE argument,
        /// not RNG luck: a shrunk arena's Outer ring has circumference
        /// ≈2·π·8 ≈ 50 m; each container's own exclusion claims ≈0.9 m
        /// (2·Hero.Radius) of arc, so AT MOST ≈55 non-overlapping
        /// containers can ever fit on it, by ANY placement algorithm.
        /// Requesting 60 — past that geometric ceiling — GUARANTEES at
        /// least 5 refusals if (and only if) the "other container"
        /// exclusion is real; without it all 60 succeed regardless of seed.
        [Test]
        public void ExcessCrateCount_ForcesSomeSkips()
        {
            var cfg = TestConfigs.Open();
            TestConfigs.ShrinkArena(ref cfg, 10f); // Outer ring -> 10 - SpawnRingInset(2) = 8 m
            cfg.Loot.CrateCount = 60; // past the ~55-container geometric ceiling on this ring

            var w = new SimulationWorld(1, cfg);

            Assert.Less(w.ContainerCount, cfg.Loot.CrateCount,
                "60 crates cannot fit on an ~50 m ring without overlapping — some must be refused");
            Assert.Greater(w.WorldStats.ContainerSpawnsSkipped, 0);
        }

        /// My own witness for R-108 (the zero-count guard must run BEFORE
        /// Geometry.ZoneSpawnRingRadius, which throws a named refusal for
        /// Middle/Core on a zoneless arena, R-64) — CacheCountMiddle/Core
        /// stay at Open()'s own default of 0 (the norm for every fixture in
        /// the whole suite), while CrateCount=1 forces the SAME
        /// construction to actually run PlaceZone(Outer, ...) for real
        /// (Outer needs no ZoneRadius at all), so the guard's own branch is
        /// exercised in the SAME call, not merely inferred by absence.
        [Test]
        public void ZonelessArena_WithZeroCacheCounts_ConstructsWithoutThrowing()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ZoneRadius = System.Array.Empty<float>();
            cfg.Arena.ZoneWallCount = 0;
            cfg.Loot.CrateCount = 1; // Outer -- legal on a zoneless arena; Loot.CacheCountMiddle/Core
                                      // already 0 (Open()'s own default) -- the R-108 guard's own case

            SimulationWorld w = null;
            Assert.DoesNotThrow(() => w = new SimulationWorld(1, cfg),
                "R-108: Middle/Core must be skipped BEFORE Geometry.ZoneSpawnRingRadius runs on a " +
                "zoneless arena (that method throws a named refusal there, R-64)");

            Assert.AreEqual(1, w.ContainerCount,
                "witness: construction must have actually run PlaceZone(Outer, ...) and placed the " +
                "one requested crate — proving real logic ran, not that everything was silently skipped");
        }
    }
}
