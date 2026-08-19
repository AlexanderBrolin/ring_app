using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Loot
{
    /// Stage 3 Task 14 (spec §3.7, Р229): the container's own policy — how
    /// long a freshly spawned container lives before it's the one shared
    /// home of "how long does a container of THIS kind live" (mirrors
    /// LootDrops' role for pickup-amount policy) and the per-tick TTL-decay
    /// pass (mirrors PickupSystem.AdvanceTtl's shape) — the two live
    /// together in one file because Т14 is a single task standing up both,
    /// unlike pickups' own history where LootDrops (Т3) and PickupSystem
    /// (also Т3, but a separate concern from day one) were already two
    /// files. A future task is free to split this one the same way if it
    /// grows a third responsibility (rule 4 — split when outgrown, not
    /// pre-emptively).
    public static class ContainerStore
    {
        /// The Ttl a freshly spawned container of `kind` starts at (spec
        /// Interfaces: "0 = не истекает (ящик, тайник, труп сборщика)").
        /// Coordinator R-100: this is the ONLY place in the whole codebase
        /// that reads `Kind` to decide anything — SpawnContainer stores it
        /// as inert data, ContainerSlotAt/TryTakeFromContainer never look
        /// at it at all. Spec §3.7 is explicit that `Kind` distinguishes
        /// skin/spawn-table only ("три механизма вместо одного дали бы три
        /// state-машины и три набора гонок") — a second branch on `Kind`
        /// anywhere else is reopening that decision, not extending this
        /// one.
        public static float InitialTtlFor(ContainerKind kind, in LootSimConfig cfg)
        {
            switch (kind)
            {
                case ContainerKind.Crate:
                case ContainerKind.Cache:
                case ContainerKind.PlayerCorpse:
                    return 0f;
                default: // Ground, MobCorpse
                    return cfg.ContainerTtlSeconds;
            }
        }

        /// Advances every live container's Ttl by one tick and removes the
        /// ones that cross zero — same back-to-front, in-place `ref`
        /// idiom as PickupSystem.AdvanceTtl (Ф1 fix-round B-I-5: production
        /// TTL decay writes through the live array directly, never through
        /// a `*ForTest` seam). A container whose Ttl is already `<= 0`
        /// (Crate/Cache/PlayerCorpse, permanent per InitialTtlFor above) is
        /// skipped before any decrement — it must stay at exactly 0
        /// forever, not drift negative and get swept on the very next
        /// pass. Wired into SimulationWorld.TickAll BEFORE PickupSystem.Update
        /// (coordinator R-101 — see that call site's own doc).
        public static void Update(SimulationWorld w)
        {
            for (int i = w.ContainerCount - 1; i >= 0; i--)
            {
                ref ContainerState c = ref w.Containers[i];
                if (c.Ttl <= 0f) continue; // permanent kind — never decays
                c.Ttl -= SimulationWorld.TickDt;
                if (c.Ttl <= 0f) w.RemoveContainerAt(i);
            }
        }

        /// Stage 3 Task 15 (spec §3.7, Р262): the constructor's OWN startup
        /// placement — CrateCount crates in Outer, CacheCountMiddle/
        /// CacheCountCore caches in Middle/Core. Called ONCE, from
        /// SimulationWorld's constructor, after every entity array
        /// (including _containers/_containerSlots) exists and after player
        /// depenetration has already run.
        ///
        /// Zone-major order (Outer, then Middle, then Core — Zone's own
        /// enum order, coordinator R-50): the SAME order WaveSystem's own
        /// zone loop and HashWave already use. Not a style choice — it is
        /// the order _lootRng is drawn from and _nextEntityId is handed
        /// out, both of which enter the replay/save contract, so the order
        /// is part of this method's contract, not an implementation detail.
        ///
        /// Content (Stage 3 Task 16, spec §3.7): "1-2 items of the zone's
        /// own tier, plus a repair kit at 25% chance" — rolled by
        /// PlaceZone below through LootDrops.RollTierItems/
        /// TryRollRepairKit, the SAME shared home DamageMob's own item
        /// drop uses (rule 2). Historical note (Т15): this doc used to say
        /// every container placed here was EMPTY, content being Т16's
        /// unstarted job — that sentence is now stale, kept only so a
        /// reader following an old cross-reference lands somewhere true.
        internal static void PlaceStartingContainers(SimulationWorld w)
        {
            ArenaSimConfig arena = w.Config.Arena;
            LootSimConfig loot = w.Config.Loot;
            float spawnRingInset = w.Config.Wave.SpawnRingInset;

            PlaceZone(w, in arena, in loot, spawnRingInset, Zone.Outer, ContainerKind.Crate, loot.CrateCount);
            PlaceZone(w, in arena, in loot, spawnRingInset, Zone.Middle, ContainerKind.Cache, loot.CacheCountMiddle);
            PlaceZone(w, in arena, in loot, spawnRingInset, Zone.Core, ContainerKind.Cache, loot.CacheCountCore);
        }

        /// One zone's own share of the startup placement — `count`
        /// independent searches, each through the shared
        /// Core.SpawnPlacement.TryFind (coordinator R-102/R-11), the SAME
        /// "candidates from RNG -> RNG-free fallback grid -> refusal" home
        /// WaveSystem's own mob spawns go through.
        ///
        /// Coordinator R-108: the zero-count guard runs BEFORE
        /// Geometry.ZoneSpawnRingRadius — that method throws a named
        /// refusal for Middle/Core on a zoneless arena (Arena.ZoneRadius.
        /// Length &lt; 2, R-64's own guard), and a zoneless arena that asks
        /// for zero Middle/Core containers (every fixture in the suite
        /// before this task, and any real match with Loot.CacheCountMiddle/
        /// CacheCountCore left at 0) must stay legal.
        static void PlaceZone(SimulationWorld w, in ArenaSimConfig arena, in LootSimConfig loot,
            float spawnRingInset, Zone zone, ContainerKind kind, int count)
        {
            if (count <= 0) return; // R-108 — guard BEFORE the call below

            // Coordinator R-105: the zone's own spawn ring, the SAME
            // arithmetic WaveSystem.TryFindSpawnPos and SimConfigBuilder's
            // own wave-spawn-ring rule already use — one home, not a
            // parallel copy (Geometry.ZoneSpawnRingRadius).
            float ringRadius = Geometry.ZoneSpawnRingRadius(zone, in arena, spawnRingInset);
            // Coordinator R-104: the container's own body radius for
            // clearance purposes is Hero.Radius — primitives are monotonic
            // in radius, and the zonal spawn ring is already validated
            // clear for a LARGER body (0.8, ZoneConfigTests.
            // Layout_EveryZoneWaveSpawnRingHasAFreeSlot), so the RNG-free
            // fallback grid is guaranteed to find room. No new balance
            // number is introduced (the Ф3 data-delivery gate is spent,
            // Т13; CR 6 forbids a balance number living in code instead).
            float radius = w.Config.Hero.Radius;
            // Stage 3 Task 16 (spec §3.7): the zone's own tier — Outer=1,
            // Middle=2, Core=3 (Zone's own declared order). Unlike the
            // archetype drop roll (LootDrops.TryRollMobItemTier), this is
            // NOT gated by DropChance at all — a crate/cache's content is
            // an UNCONDITIONAL "1-2 items", DropChance only governs whether
            // a Chaser/Gunner/Elite drops an item on death (coordinator
            // finding, session 30: the spec table lists "1-2 предмета" as
            // a count rule for containers, a percentage only for
            // archetypes). The gate that keeps this golden-safe is the
            // EXISTING zero-count guard above (R-108), not a new one.
            byte tier = (byte)((int)zone + 1);
            // Coordinator R-131: one stack buffer for the whole zone's
            // loop, hoisted OUTSIDE the per-container loop below rather
            // than allocated per-iteration — 2 trophies + a possible
            // repair kit, same "call outside the hot path, reuse the
            // buffer" shape SplitByZones' own stackalloc follows.
            System.Span<byte> items = stackalloc byte[3];

            for (int i = 0; i < count; i++)
            {
                ref Random rng = ref w.LootRng;
                var filter = new ContainerSpawnFilter(w, in arena, radius);
                if (SpawnPlacement.TryFind(ref rng, loot.LootSpawnAttempts, loot.LootFallbackSlots,
                        ringRadius, in filter, out float2 pos))
                {
                    int n = LootDrops.RollTierItems(tier, w.Config.Items, ref rng, items);
                    if (LootDrops.TryRollRepairKit(loot.RepairKitChance, w.Config.Items, ref rng, out byte kitId))
                        items[n++] = kitId;
                    w.SpawnContainer(kind, pos, items.Slice(0, n));
                }
                else
                {
                    // Coordinator: the SEARCH's own skip counter — a SECOND,
                    // independent path to the same WorldStats field exists
                    // inside SimulationWorld.SpawnContainer itself (the
                    // MaxContainers cap) — both are legal, but they are TWO
                    // branches, each with its own mutation witness.
                    w.WorldStatsRef.ContainerSpawnsSkipped++;
                }
            }
        }

        /// Stage 3 Task 15 (coordinator R-102/R-106): the container's own
        /// ISpawnFilter. The geometry half (obstacles, walls, zone-wall
        /// arcs) delegates to Core.SpawnPlacement.GeometryBlocked with
        /// `doorsPassable: false` — Geometry.InArcBand, a pure RADIAL band
        /// test with NO angular door exception — because a container, unlike
        /// a mob, must not be allowed to sit inside a doorway and block it
        /// (coordinator R-106; the test name `NoContainerInsideArcOrDoor`
        /// reads literally). The half that does NOT come from
        /// WaveSystem's own filter — distance-to-player, live-mob overlap —
        /// is replaced by this filter's own concern: overlap with an
        /// ALREADY-PLACED container (coordinator §2: "контейнерный фильтр
        /// добавляет своё: перекрытие с другим контейнером").
        readonly struct ContainerSpawnFilter : ISpawnFilter
        {
            readonly SimulationWorld _w;
            readonly ArenaSimConfig _arena;
            readonly float _radius;

            public ContainerSpawnFilter(SimulationWorld w, in ArenaSimConfig arena, float radius)
            {
                _w = w;
                _arena = arena;
                _radius = radius;
            }

            public bool IsValid(float2 pos)
            {
                if (SpawnPlacement.GeometryBlocked(in _arena, pos, _radius, doorsPassable: false))
                    return false;

                ContainerState[] containers = _w.Containers;
                int count = _w.ContainerCount;
                for (int i = 0; i < count; i++)
                    if (Geometry.CircleOverlap(pos, _radius, containers[i].Pos, _radius))
                        return false;

                return true;
            }
        }
    }
}
