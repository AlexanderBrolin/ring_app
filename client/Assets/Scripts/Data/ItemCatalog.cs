using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Data
{
    /// The item catalog (Stage 3 Task 13, spec §3.7): a flat, plain list of
    /// `ItemDef` records — tier, slot cost, credit value, kind. Copied
    /// array-for-array into SimConfig.Items by SimConfigBuilder.Build
    /// (Simulation never sees this class, CRITICAL RULE 1 — `ItemDef`/
    /// `ItemKind` live in Ring.Simulation.Core and this class only holds an
    /// array of that already-Simulation-owned type, no parallel DTO).
    ///
    /// FIVE records ship today, not the six spec §3.7's own prose claims —
    /// owner decision R-91: the prose and the spec's own data table
    /// disagree (the table lists exactly Т1/Т2/Т3/Т4/ремкомплект), and Р228
    /// ("тир предмета — тир зоны смерти") makes tier-of-drop a FUNCTION —
    /// a second trophy sharing a tier would need a tie-break rule the spec
    /// states nowhere (either an extra `_lootRng` draw, moving determinism
    /// for nothing, or a silent "first match", both worse than not shipping
    /// a record nothing in this stage can ever select). The amendment to
    /// spec §3.7's prose is Т39's job; a sixth catalog record, if the owner
    /// wants one, is a milestone В1 data decision, not this task's.
    ///
    /// Ids are assigned by DECLARATION ORDER here and are NOT required to
    /// equal their array index once the catalog grows past this task
    /// (SimConfigBuilder.Validate rejects a duplicate id, never a gap) —
    /// every reader resolves an id through ItemCatalogLookup.Find, never
    /// its own search.
    [CreateAssetMenu(menuName = "Ring/Item Catalog", fileName = "ItemCatalog")]
    public sealed class ItemCatalog : ScriptableObject
    {
        public ItemDef[] Items =
        {
            // Т1 — logistics scrap (spec §3.7 table). Id 1, not 0 — 0 is
            // reserved as the container slot's own "empty" sentinel
            // (SimulationWorld.TryTakeFromContainer, coordinator fix-round
            // Ф3 review C1) since Т16 gave this catalog a live take-path;
            // every id below shifted up by one for the same reason.
            new ItemDef { Id = 1, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
            // Т2 — power/comms assemblies.
            new ItemDef { Id = 2, Tier = 2, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
            // Т3 — corporate assets.
            new ItemDef { Id = 3, Tier = 3, SlotCost = 3, CreditValue = 200, Kind = ItemKind.Trophy },
            // Т4 — the Director's memory core (spec: "один за заход"). The
            // one-per-match cap is held by the invariant "the Director
            // spawns at most once per match" — that invariant does not
            // exist yet (it arrives with the Director's own spawn, Т22);
            // Т16 only implemented "exactly one core per Director DEATH",
            // which is the same guarantee for as long as there is at most
            // one Director. The catalog itself states only the record.
            new ItemDef { Id = 4, Tier = 4, SlotCost = 4, CreditValue = 1000, Kind = ItemKind.Trophy },
            // The repair kit — outside the tier ladder (Tier 0), no credit
            // value (spec §3.7 table: "0").
            new ItemDef { Id = 5, Tier = 0, SlotCost = 1, CreditValue = 0, Kind = ItemKind.RepairKit },
        }; // sync-marker key — keep LAST (this class's only field)

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
