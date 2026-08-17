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
        /// A mob's energy-cell drop on death (spec §3.6 "Дроп ячеек"): today
        /// a single fixed-per-archetype count read straight off config.
        /// MobSimConfig.CellsOnDeath is a TEMPORARY home (R-3) — Т13 moves it
        /// into LootSimConfig.CellsPerMob (indexed by MobType) in one step.
        public static int MobDeathCells(in MobSimConfig cfg) => cfg.CellsOnDeath;

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
        /// the instant any player died in one. WeaponSimConfig.
        /// CorpseCellFraction is a TEMPORARY home (R-3) — Т13 moves it into
        /// LootSimConfig.CorpseCellFraction.
        public static int CorpseCells(int ammo, in WeaponSimConfig cfg)
        {
            if (ammo <= 0 || cfg.CorpseCellFraction <= 0f) return 0;
            int raw = (int)math.floor(ammo * cfg.CorpseCellFraction / cfg.ShotsPerCell);
            return math.max(raw, 1);
        }
    }
}
