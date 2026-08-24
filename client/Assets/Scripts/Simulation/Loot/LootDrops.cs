using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Loot
{
    /// Stage 3 Task 3 (spec §3.6, errata E-6 C-I10): the ONE shared home for
    /// both of this task's drop-amount computations — a mob's fixed
    /// per-archetype drop and a dead player's corpse fraction — so
    /// SimulationWorld.DamageMob and SimulationWorld.KillPlayer call in
    /// rather than each keeping its own copy of the arithmetic (rule 2,
    /// reuse over duplication).
    public static class LootDrops
    {
        /// A mob's energy-cell drop on death (spec §3.6 "Дроп ячеек"): a
        /// single fixed-per-archetype count, indexed by MobType out of
        /// LootSimConfig.CellsPerMob. Stage 3 Task 13 (R-3): replaces the
        /// TEMPORARY per-archetype MobSimConfig.CellsOnDeath field (one
        /// MobSimConfig instance per archetype, same field name) with one
        /// flat array — no second home of "how many cells does archetype X
        /// drop" anywhere. Dense positional indexing by a closed four-value
        /// enum (Chaser/Gunner/Elite/Director), unlike the sparse,
        /// gap-tolerant byte id space ItemCatalogLookup.Find resolves — no
        /// named-refusal search needed here, no bounds guard here either.
        /// The array IS always exactly four long for any SimConfig built by
        /// SimConfigBuilder.Build (coordinator R-96): ValidateLoot enforces
        /// it when a real `loot` asset is supplied, and Build's own
        /// omitted-`loot` branch seeds a correctly-sized all-zero array
        /// rather than leaving the field null. A hand-built SimConfig
        /// fixture that skips Build entirely (every test in this codebase
        /// mirrors the four-element shape by construction) is outside what
        /// either guarantee can reach.
        public static int MobDeathCells(MobType type, in LootSimConfig cfg) => cfg.CellsPerMob[(int)type];

        /// A dead player's corpse drop (spec §3.6): floor(ammo *
        /// CorpseCellFraction / ShotsPerCell) cells, minimum ONE while the
        /// corpse carried ANY ammo AND the fraction is genuinely positive —
        /// spec's own reasoning is that killing an almost-dry player must
        /// still read as a drop, not nothing, or the killer can't tell
        /// whether a drop happened at all. The minimum must NOT fire when
        /// CorpseCellFraction is deliberately zero (owner decision R-18):
        /// TestConfigs' own golden-safety fixture sets it to exactly 0 so
        /// NO pickup is ever born in either golden scenario, and a minimum
        /// that ignored a zero fraction would silently break that premise
        /// the instant any player died in one. Stage 3 Task 13 (R-3):
        /// ShotsPerCell stays on WeaponSimConfig (the ammo economy, not
        /// loot); CorpseCellFraction moved to LootSimConfig — two config
        /// sections in, not one, because the number itself moved out from
        /// under the other.
        public static int CorpseCells(int ammo, in WeaponSimConfig weaponCfg, in LootSimConfig lootCfg)
        {
            if (ammo <= 0 || lootCfg.CorpseCellFraction <= 0f) return 0;
            int raw = (int)math.floor(ammo * lootCfg.CorpseCellFraction / weaponCfg.ShotsPerCell);
            return math.max(raw, 1);
        }

        /// Stage 3 Task 16 (spec §3.7): whether `type`'s own death produces
        /// an item — Chaser/Gunner/Elite only, indexed into
        /// LootSimConfig.DropChance by `[archetype * Zones.Count + zone]`
        /// (errata E-6 A-I11). Zones.Count = 3 is Zone's own declared
        /// Outer/Middle/Core order (DropChance's own field doc,
        /// Core/SimConfig.cs, states the same fact — wave-cadence-per-zone
        /// (bd app-ggvz Т1) retired this method's own second copy of the
        /// count in favor of that one shared home, rule 2). The Director
        /// never reaches this method — its own drop is a fixed rule, not a
        /// chance roll (coordinator R-126), handled by DamageMob's own
        /// separate branch.
        ///
        /// Golden risk R-120 (coordinator §1а): the archetype's own
        /// DropChance ROW is checked for all-zero BEFORE `Geometry.ZoneOf`
        /// is ever called — ZoneOf's own ZoneRadius[0]/[1] reads
        /// (Geometry.cs:297) carry no bounds guard of their own and throw a
        /// bare IndexOutOfRangeException on a legal zoneless arena (R-53).
        /// SimConfigBuilder.ValidateLoot's own R-121b rule guarantees that
        /// a NONZERO row implies Arena.ZoneRadius.Length == 2, so by the
        /// time this method reaches ZoneOf that guarantee already holds for
        /// any SimConfig built by SimConfigBuilder.Build — a hand-built
        /// fixture that skips Build (every test in this suite) is outside
        /// what that guarantee can reach, which is exactly why the row
        /// check has to run here too, not only in Validate.
        public static bool TryRollMobItemTier(MobType type, float2 pos, in ArenaSimConfig arena,
            in LootSimConfig loot, ref Random rng, out byte tier)
        {
            int rowOffset = (int)type * Zones.Count;
            if (loot.DropChance[rowOffset] <= 0f && loot.DropChance[rowOffset + 1] <= 0f &&
                loot.DropChance[rowOffset + 2] <= 0f)
            {
                tier = 0;
                return false;
            }
            Zone zone = Geometry.ZoneOf(pos, in arena);
            float chance = loot.DropChance[rowOffset + (int)zone];
            if (chance <= 0f || rng.NextFloat() >= chance)
            {
                tier = 0;
                return false;
            }
            tier = TierOfZone(zone);
            return true;
        }

        /// Stage 3 Task 16 (Р228: "тир предмета — тир зоны смерти"),
        /// coordinator fix-round (Ф3 review m6): the ONE home for "zone ->
        /// tier" — Zone's own declared order (Outer=0/Middle=1/Core=2) maps
        /// onto tier 1..3 by a plain +1, no separate table. Shared by this
        /// method's own roll above and ContainerStore.PlaceZone's starting
        /// crate/cache content — a second copy of the same +1 had drifted
        /// silently between the two before this fix-round noticed it (m6).
        public static byte TierOfZone(Zone zone) => (byte)((int)zone + 1);

        /// Stage 3 Task 16 (spec §3.7): rolls 1 or 2 copies of the ONE
        /// Trophy item mapped to `tier` (ItemCatalogLookup.FindByTier,
        /// R-124) into `buffer`, returns the count written — shared by
        /// ContainerStore.PlaceZone's own starting crate/cache content and
        /// DamageMob's Director branch (three tier-3 containers). Same item
        /// repeated up to twice — R-124's own "one trophy per tier" rule
        /// means there is exactly one candidate id, so "which item" is not
        /// a second draw, only "how many".
        public static int RollTierItems(byte tier, ItemDef[] catalog, ref Random rng, System.Span<byte> buffer)
        {
            byte id = ItemCatalogLookup.FindByTier(tier, catalog).Id;
            int count = rng.NextInt(1, 3); // {1, 2}
            for (int i = 0; i < count; i++) buffer[i] = id;
            return count;
        }

        /// Stage 3 Task 16 (spec §3.7): the repair kit riding alongside a
        /// crate/cache's own main content ("сверх основного содержимого") —
        /// never for a mob corpse or the Director's own containers (spec's
        /// own table names ONLY ящик/тайник). Guarded at `chance <= 0f`
        /// before the draw (same discipline as CorpseCells' own fraction
        /// guard above) so a golden-safety-zeroed chance costs `_lootRng`
        /// nothing — golden safety here rests on Loot.CrateCount/
        /// CacheCount* staying zero (the existing R-108 guard in
        /// ContainerStore.PlaceZone), not on this one, but the discipline
        /// is kept anyway for the same "no meaningless draw" reason.
        public static bool TryRollRepairKit(float chance, ItemDef[] catalog, ref Random rng, out byte itemId)
        {
            if (chance <= 0f || rng.NextFloat() >= chance)
            {
                itemId = 0;
                return false;
            }
            itemId = ItemCatalogLookup.FindRepairKit(catalog).Id;
            return true;
        }
    }
}
