namespace Ring.Simulation.Loot
{
    /// One player's backpack (Stage 3 Task 4, spec §3.6 "Рюкзак") — a flat
    /// array of item ids, no catalog metadata yet (Т13). Capacity is
    /// measured in SLOT POINTS, not item count: TryAdd sums SlotCostOf
    /// across the carried items and refuses an add that would push the
    /// total past the caller-supplied capacity.
    ///
    /// Lives outside PlayerState (owner decision Р232, spec Interfaces):
    /// PlayerState is copied wholesale into ReconcileData, snapshot
    /// fixtures and prediction, so an array field inside it would make
    /// every one of those copies allocate, breaking "zero allocations per
    /// tick". SimulationWorld instead owns one Inventory instance per
    /// player, next to _matchStats, sized once at construction to
    /// HeroSimConfig.MaxInventoryItems — the hard ceiling on item COUNT
    /// (independent of slot points, so a future catalog of very cheap
    /// items still cannot outgrow the backing array) — and never resizes
    /// it, same "preallocated, capped array" shape as MobState/
    /// ProjectileState/PickupState. Both capacity numbers are TOPOLOGY
    /// (owner decision R-19, spec Р286/Р287) — SimulationWorld.ApplyConfig
    /// rejects any hot-tweak that changes either one, same
    /// ArenaTopologyMatches contract as Arena.MaxPickups.
    public sealed class Inventory
    {
        readonly byte[] _items;
        int _count;

        public Inventory(int maxItems)
        {
            _items = new byte[maxItems];
        }

        public int Count => _count;

        /// Reads whatever is at `slot` — same "no bounds guard beyond the
        /// backing array" contract as SimulationWorld.Pickups/Mobs; callers
        /// stay within [0, Count) exactly like every other indexed read in
        /// this codebase.
        public byte ItemAt(int slot) => _items[slot];

        /// Sum of SlotCostOf across every carried item — what TryAdd checks
        /// against the caller's capacity, not Count itself (a heavier item
        /// can cost more than one slot point once the real catalog lands
        /// in Т13).
        public int UsedSlots()
        {
            int total = 0;
            for (int i = 0; i < _count; i++) total += SlotCostOf(_items[i]);
            return total;
        }

        /// Refuses (returns false, leaves the backpack byte-for-byte
        /// unchanged) once the item's own SlotCostOf would push UsedSlots
        /// past `capacity`, OR the backing array is already at its hard
        /// MaxInventoryItems ceiling — whichever bites first. `capacity` is
        /// a parameter rather than a stored field so a hot-tweak to
        /// Hero.InventoryCapacity is honored the next call, same
        /// "read fresh from SimConfig every time" contract
        /// Loot.PickupSystem.Collect's own PickupRadius read follows.
        public bool TryAdd(byte itemId, int capacity)
        {
            if (_count >= _items.Length) return false;
            if (UsedSlots() + SlotCostOf(itemId) > capacity) return false;
            _items[_count++] = itemId;
            return true;
        }

        /// Swap-remove — same idiom as SimulationWorld.RemovePickupAt/
        /// RemoveProjectileAt: O(1), no shifting. `slot` outside
        /// [0, Count) is refused, not thrown — an out-of-range removal is
        /// exactly as ordinary an outcome as removing from an empty
        /// backpack.
        public bool TryRemoveAt(int slot, out byte itemId)
        {
            if (slot < 0 || slot >= _count)
            {
                itemId = 0;
                return false;
            }
            itemId = _items[slot];
            _items[slot] = _items[--_count];
            return true;
        }

        /// Test-only seam: overwrites the whole backpack with exactly the
        /// given items (`items.Length` must not exceed the backing array's
        /// own MaxInventoryItems length, same contract TryAdd enforces one
        /// item at a time).
        public void SetForTest(params byte[] items)
        {
            System.Array.Copy(items, _items, items.Length);
            _count = items.Length;
        }

        /// Deep-copies this backpack's live contents into a fresh instance
        /// — SimulationWorld.SaveState's per-player counterpart to
        /// System.Array.Copy(_players, save.Players, ...): Inventory is a
        /// reference type, so a save that aliased the live instance instead
        /// of cloning it would let a later live mutation corrupt a save
        /// already taken, breaking WorldSave's own "deep copy" contract.
        /// Allocates — same "call outside the hot tick path" contract
        /// SaveState's own doc states.
        public Inventory Clone()
        {
            var copy = new Inventory(_items.Length);
            System.Array.Copy(_items, copy._items, _items.Length);
            copy._count = _count;
            return copy;
        }

        /// Overwrites this instance's contents from `source` in place —
        /// SimulationWorld.RestoreState's counterpart to Clone above. Copies
        /// INTO the live instance rather than replacing the array reference,
        /// so the live world's Inventory objects keep their identity across
        /// a restore, same as every other in-place RestoreState field.
        public void RestoreFrom(Inventory source)
        {
            System.Array.Copy(source._items, _items, _items.Length);
            _count = source._count;
        }

        /// TEMPORARY (T4 -> T13): the real cost comes from ItemCatalog.
        public static int SlotCostOf(byte itemId) => 1;
    }
}
