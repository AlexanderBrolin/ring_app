using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// The loot window (Stage 3 Т32б, spec §3.11 С23): the source box on the
    /// left, this collector's own backpack on the right, and the world still
    /// playing between them.
    ///
    /// NO PAUSE, AND THAT IS THE WHOLE DESIGN. The spec asks for a window a
    /// player opens while mobs are still coming, so the panels are pushed to
    /// the screen edges and the middle is left alone. `SimInput.InventoryOpen`
    /// is what makes the choice cost something — it slows the step and forbids
    /// the shot, on the SERVER (Т20) — so the risk is real rather than
    /// decorative, and this class never has to enforce it.
    ///
    /// A READER, LIKE EVERY OTHER VIEW HERE. It draws `Curr` and turns a click
    /// into `SimulationRunner.TryRequestLoot`. Nothing is predicted (CR 3): a
    /// pressed slot dims and waits for the world to say what happened, and on a
    /// local backend that wait is one call long.
    ///
    /// THE SOURCE BOX IS WHICHEVER ONE THE FRAME DESCRIBES. The frame's
    /// interior pool lists exactly the boxes within `LootRadius` — the server
    /// sends no others (Р238) and the local capture keeps the same rule — so
    /// "which box am I standing over" needs no distance test here and no config
    /// access: it is the pool's first record. An empty pool therefore says the
    /// player walked away from every box, and the SOURCE COLUMN empties — the
    /// window does not.
    ///
    /// TWO PANELS, TWO LIVES (bd `app-17gj`, the owner's В1 playtest; lesson
    /// 399). The first cut of this class closed the whole window on an empty
    /// pool, which read "the source has nothing to show" as "the window must
    /// close" — and since open ground is the common case, Tab lowered the flag
    /// it had just raised and no panel ever appeared. The backpack belongs to
    /// the collector, not to the box he happens to be standing on, so it is
    /// readable anywhere; what shuts the window is `WindowMustClose` (death,
    /// extraction), the sampler's own dash/slide edges, the pause menu, and Tab.
    /// The cost is deliberate rather than incidental: `SimInput.InventoryOpen`
    /// slows the step and forbids the shot for as long as the window is up, so
    /// a pack read in open ground is paid for in the open.
    public sealed class InventoryWindowController : MonoBehaviour
    {
        /// Colors are literals rather than `GameFeelConfig` fields, the same
        /// route the slide dust and the pickup flash took: they are UI paint,
        /// not numbers a match is decided by (CR 6 is about the latter). A
        /// tuning pass that wants them live can lift them the way
        /// `PickupVisualDiameter` was lifted.
        static readonly Color SlotIdle = new Color(0.16f, 0.17f, 0.20f, 0.92f);
        static readonly Color SlotEmpty = new Color(0.10f, 0.10f, 0.12f, 0.55f);
        static readonly Color SlotPending = new Color(0.22f, 0.30f, 0.38f, 0.95f);
        static readonly Color SlotRefused = new Color(0.45f, 0.13f, 0.13f, 0.95f);

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameObject _panel;
        [SerializeField] TMP_Text _sourceTitle;
        [SerializeField] TMP_Text _backpackTitle;
        /// One entry per CONTAINER slot and one per BACKPACK slot, built once
        /// by the bootstrap from `Arena.MaxContainerSlots` and
        /// `Hero.MaxInventoryItems`. Arrays rather than a spawned list: this
        /// layer does not allocate per frame, and both counts are caps the
        /// config already fixes.
        [SerializeField] Button[] _sourceSlots;
        [SerializeField] TMP_Text[] _sourceLabels;
        [SerializeField] Image[] _sourceProgress;
        [SerializeField] Button[] _backpackSlots;
        [SerializeField] TMP_Text[] _backpackLabels;
        [SerializeField] AudioSource _audio;
        [SerializeField] AudioClip _refusalClip;

        /// The refusal already sounded for, so one refused click makes one
        /// noise. `LastLootRefusal` is a LEVEL — it keeps reporting the same
        /// verdict until the next answered request — so an edge has to be
        /// derived, and it is derived from the whole address rather than from
        /// the code alone: two refusals of the same kind on two different slots
        /// are two events.
        LootRefusal _soundedRefusal;
        int _soundedContainerId;
        int _soundedSlot;

        void Awake()
        {
            // The listeners close over the slot index, which is why they are
            // wired once here rather than rebuilt in `Update`: rebuilding them
            // per frame would allocate a delegate per slot per frame on a path
            // that must not allocate.
            for (int i = 0; i < _sourceSlots.Length; i++)
            {
                int slot = i;
                _sourceSlots[i].onClick.AddListener(() => TakeFromSource(slot));
            }

            for (int i = 0; i < _backpackSlots.Length; i++)
            {
                int index = i;
                _backpackSlots[i].onClick.AddListener(() => UseOrDrop(index));
            }
        }

        /// Whether the world itself has ended the window this frame — the
        /// client half of `SimInputSanitizer`'s own list (Т20), which already
        /// forces the SERVER's `SimInput.InventoryOpen` down for a dead or
        /// extracted collector. Without this the flag would stay raised on a
        /// client the world has stopped honoring, and the panels would keep
        /// drawing over a corpse.
        ///
        /// IT DOES NOT ASK THE SOURCE POOL, AND THAT IS THE WHOLE FIX
        /// (bd `app-17gj`, the owner's В1 playtest). The pool describes the
        /// boxes within `LootOps.WithinLootReach` and nothing else, so reading
        /// "the pool is empty" as "the window must close" made open ground
        /// close a window Tab had just opened. Two statements, two homes: this
        /// one answers whether the WINDOW lives, `DrawSource` answers what the
        /// SOURCE panel has to show.
        ///
        /// THE OTHER THREE CLOSINGS ARE NOT HERE BECAUSE THEY ARE NOT ABOUT THE
        /// FRAME. Dash and slide are edges, and `InputSampler.ClearLatches`
        /// shuts the window where it sees them; Escape belongs to
        /// `PauseController` and arrives through `_runner.Paused`; Tab is the
        /// toggle itself.
        public static bool WindowMustClose(in RenderSnapshot frame)
            => !frame.Player.Alive || frame.Player.Extracted;

        /// Stage 3 Т35 (spec Р291, the restart reset list): a window open when
        /// the raid ends must not be open when the next one begins.
        ///
        /// THE FLAG IS LOWERED, NOT ONLY THE PANEL HIDDEN, because the flag is
        /// what costs something: `SimInput.InventoryOpen` slows the step and
        /// forbids the shot for as long as it is up, and a fresh raid that
        /// opened with an invisible window would have its collector walking
        /// slowly and unable to fire, with nothing on screen to explain it.
        void OnEnable()
        {
            if (_runner != null) _runner.WorldRestarted += HandleWorldRestarted;
        }

        void OnDisable()
        {
            if (_runner != null) _runner.WorldRestarted -= HandleWorldRestarted;
        }

        void HandleWorldRestarted()
        {
            _runner.CloseInventory();
            Hide();
            // The refusal echo belongs to the match that produced it: a code
            // still latched here would sound on the first click of the next
            // raid, about a box that no longer exists.
            _soundedRefusal = LootRefusal.None;
            _soundedContainerId = 0;
            _soundedSlot = 0;
        }

        void Update()
        {
            if (_runner == null || _panel == null) return;

            // THE PAUSE MENU CLOSES THE WINDOW, which is how Escape reaches it
            // (spec §3.11 lists Escape among the closings). `PauseController`
            // owns that key already — the fixed dev-controller exception of
            // П-6 — and a second reader of the same key would be two owners of
            // one input.
            if (!_runner.Ready || _runner.Paused)
            {
                Hide();
                return;
            }

            if (!_runner.InventoryOpen)
            {
                Hide();
                return;
            }

            RenderSnapshot frame = _runner.Curr;
            if (WindowMustClose(in frame))
            {
                // The window is TOLD to close rather than merely hidden, so the
                // flag stops slowing the step and forbidding the shot as well.
                _runner.CloseInventory();
                Hide();
                return;
            }

            _panel.SetActive(true);
            // WHICHEVER BOX THE FRAME DESCRIBES, OR NONE. An empty pool is open
            // ground, and open ground is a place a collector is entitled to
            // stand and count his own cells in (`app-17gj`) — the source column
            // simply has nothing in it.
            bool hasSource = frame.ContainerInteriorCount > 0;
            ContainerInterior source = hasSource
                ? frame.ContainerInteriors[0]
                : default;
            DrawSource(in frame, in source, hasSource);
            DrawBackpack(in frame);
            SoundRefusal();
        }

        void Hide()
        {
            if (_panel.activeSelf) _panel.SetActive(false);
        }

        /// The box's slots: what each holds, how far a transfer out of it has
        /// got, and whether the last refusal belongs to it.
        ///
        /// THE MASK INDEXES THE POOL, NOT THE OTHER WAY AROUND. Bit `i` set
        /// means slot `i` is occupied and its item is the NEXT one in the
        /// record's stretch of the pool — the wire's own contract, and the
        /// reason an item id cannot simply be read at `ItemOffset + slot`.
        ///
        /// `present` IS PASSED RATHER THAN DERIVED FROM `source`, because the
        /// absent case is a `default` record and its `Id` is 0 — the very
        /// address `Drop` and `Use` travel with (`BackpackAddress`). Asking
        /// `RefusalBelongsTo(0, slot)` would light a SOURCE slot red for a
        /// refusal the BACKPACK earned, which is the one way the empty column
        /// could still tell a lie. The transfer bar needs no such guard and is
        /// not given one: `LootOps.Begin` sets `LootTargetContainerId` on the
        /// `Take` path ALONE (every other op throws before reaching it), so a
        /// running `LootTimer` always names a real box, never id 0.
        void DrawSource(in RenderSnapshot frame, in ContainerInterior source, bool present)
        {
            SimConfig cfg = _runner.Config;
            _sourceTitle.text = present ? $"Источник · {source.ItemCount}" : "Источник · нет";

            PlayerState self = frame.Player;
            int pooled = 0;
            for (int slot = 0; slot < _sourceSlots.Length; slot++)
            {
                bool occupied = slot < ContainerInteriorSlots
                    && (source.OccupancyMask & (1 << slot)) != 0;
                byte itemId = 0;
                if (occupied && pooled < source.ItemCount)
                {
                    itemId = frame.ContainerInteriorItems[source.ItemOffset + pooled];
                    pooled++;
                }

                bool transferring = self.LootTargetContainerId == source.Id
                    && self.LootTargetSlot == slot && self.LootTimer > 0f;
                _sourceProgress[slot].fillAmount = transferring
                    ? TransferProgress(in cfg, itemId, self.LootTimer)
                    : 0f;
                _sourceLabels[slot].text = occupied ? DescribeItem(in cfg, itemId) : "—";
                _sourceSlots[slot].targetGraphic.color = SlotColor(occupied, transferring,
                    present && RefusalBelongsTo(source.Id, slot));
                _sourceSlots[slot].interactable = occupied;
            }
        }

        /// The collector's own pack. `Drop` and `Use` address it by INDEX, and
        /// so does this panel: the entry at index `i` is the item
        /// `LootOps.Validate` will check when asked about `i`.
        void DrawBackpack(in RenderSnapshot frame)
        {
            SimConfig cfg = _runner.Config;
            _backpackTitle.text =
                $"Рюкзак · {frame.InventorySlotPoints}/{cfg.Hero.InventoryCapacity}";

            for (int i = 0; i < _backpackSlots.Length; i++)
            {
                bool filled = i < frame.InventoryItemCount;
                byte itemId = filled ? frame.InventoryItems[i] : (byte)0;
                _backpackLabels[i].text = filled ? DescribeItem(in cfg, itemId) : "—";
                _backpackSlots[i].targetGraphic.color = SlotColor(filled, false,
                    RefusalBelongsTo(BackpackAddress, i));
                _backpackSlots[i].interactable = filled;
            }
        }

        /// How much of the transfer out of the addressed slot is already spent,
        /// in `[0, 1]`.
        ///
        /// FROM THE ITEM'S OWN TIER, because that is what sets the duration:
        /// `LootSimConfig.TransferSeconds` is indexed by tier (spec §3.8), and
        /// dividing by any other number would draw a bar that fills at the
        /// wrong pace for every item but one.
        static float TransferProgress(in SimConfig cfg, byte itemId, float remaining)
        {
            ItemDef def = ItemCatalogLookup.Find(itemId, cfg.Items);
            int tier = def.Tier;
            if (tier < 0 || tier >= cfg.Loot.TransferSeconds.Length) return 0f;
            float total = cfg.Loot.TransferSeconds[tier];
            if (total <= 0f) return 0f;
            return Mathf.Clamp01(1f - remaining / total);
        }

        static string DescribeItem(in SimConfig cfg, byte itemId)
        {
            ItemDef def = ItemCatalogLookup.Find(itemId, cfg.Items);
            return def.Kind == ItemKind.RepairKit
                ? $"Ремкомплект · {def.SlotCost}"
                : $"Т{def.Tier} · {def.SlotCost}";
        }

        static Color SlotColor(bool occupied, bool transferring, bool refused)
        {
            if (refused) return SlotRefused;
            if (transferring) return SlotPending;
            return occupied ? SlotIdle : SlotEmpty;
        }

        bool RefusalBelongsTo(int containerId, int slot)
            => _runner.LastLootRefusal != LootRefusal.None
               && _runner.LootRequestContainerId == containerId
               && _runner.LootRequestSlot == slot;

        /// One noise per refusal, and the clip is `denied.wav` — the sound this
        /// project already uses for "the game says no" (the stamina refusal of
        /// Task 22). A second refusal sound would be a second vocabulary for
        /// one meaning.
        void SoundRefusal()
        {
            LootRefusal code = _runner.LastLootRefusal;
            int containerId = _runner.LootRequestContainerId;
            int slot = _runner.LootRequestSlot;
            if (code == LootRefusal.None)
            {
                _soundedRefusal = LootRefusal.None;
                return;
            }

            if (code == _soundedRefusal && containerId == _soundedContainerId
                && slot == _soundedSlot)
                return;

            _soundedRefusal = code;
            _soundedContainerId = containerId;
            _soundedSlot = slot;
            if (_audio != null && _refusalClip != null) _audio.PlayOneShot(_refusalClip);
        }

        void TakeFromSource(int slot)
        {
            if (_runner == null || !_runner.Ready || !_runner.InventoryOpen) return;
            RenderSnapshot frame = _runner.Curr;
            if (frame.ContainerInteriorCount <= 0) return;
            _runner.TryRequestLoot(LootOp.Take, frame.ContainerInteriors[0].Id, slot);
        }

        /// A repair kit is USED, anything else is DROPPED — one click, and the
        /// item's own kind decides which verb it means.
        ///
        /// NOT TWO MOUSE BUTTONS, because `Button.onClick` does not say which
        /// one was pressed and a second input path for one panel would be a
        /// second place to keep the addressing right. `LootOps.Validate`'s
        /// twelfth check answers `ItemNotUsable` for anything else, so the
        /// mapping cannot be silently wrong: it would come back refused, on the
        /// slot that was pressed.
        void UseOrDrop(int index)
        {
            if (_runner == null || !_runner.Ready || !_runner.InventoryOpen) return;
            RenderSnapshot frame = _runner.Curr;
            if (index >= frame.InventoryItemCount) return;

            SimConfig cfg = _runner.Config;
            ItemDef def = ItemCatalogLookup.Find(frame.InventoryItems[index], cfg.Items);
            LootOp op = def.Kind == ItemKind.RepairKit ? LootOp.Use : LootOp.Drop;
            _runner.TryRequestLoot(op, BackpackAddress, index);
        }

        /// The container id `Drop` and `Use` travel with. Their subject is the
        /// BACKPACK, not a box, and the wire still carries a container field —
        /// so it carries the one id no container has. `SimulationWorld` mints
        /// entity ids from 1, and `LootOps.Validate` never resolves a container
        /// for these two ops.
        const int BackpackAddress = 0;

        /// How many slots one container's occupancy mask can speak about — the
        /// width of the byte it is. A box configured with more slots than that
        /// has no bit for them on the wire either
        /// (`SnapshotBlocks.ContainerSlotsMaskWidth`), and this panel draws what
        /// the wire can describe.
        const int ContainerInteriorSlots = 8;
    }
}
