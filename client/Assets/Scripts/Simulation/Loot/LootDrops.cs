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
    }
}
